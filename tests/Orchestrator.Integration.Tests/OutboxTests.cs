using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Messaging;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Outbox;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class OutboxTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T11:00:00Z");

    [Fact]
    public async Task Enqueue_CommitsAtomicallyWithStateChange()
    {
        var task = new WorkTask("o", "U1", TaskSource.Slack, $"c-{Guid.NewGuid()}", Now);
        await using var db = fixture.NewDbContext();
        var writer = new OutboxWriter(db, TimeProvider.System);

        db.Tasks.Add(task);
        task.Fire(TaskTrigger.PlanningStarted, Now);
        writer.Enqueue("task.planning-started", new { taskId = task.Id }, task.Id, $"plan:{task.Id}");
        await db.SaveChangesAsync();

        await using var read = fixture.NewDbContext();
        (await read.Outbox.CountAsync(m => m.TaskId == task.Id)).ShouldBe(1);
        (await read.Tasks.SingleAsync(t => t.Id == task.Id)).State.ShouldBe(TaskState.Planning);
    }

    [Fact]
    public async Task Enqueue_RolledBackTransaction_LeavesNoMessage()
    {
        var correlation = $"c-{Guid.NewGuid()}";
        await using var db = fixture.NewDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var task = new WorkTask("o", "U1", TaskSource.Slack, correlation, Now);
        db.Tasks.Add(task);
        new OutboxWriter(db, TimeProvider.System)
            .Enqueue("task.created", new { taskId = task.Id }, task.Id, $"created:{task.Id}");
        await db.SaveChangesAsync();
        await tx.RollbackAsync();

        await using var read = fixture.NewDbContext();
        (await read.Outbox.CountAsync(m => m.TaskId == task.Id)).ShouldBe(0);
        (await read.Tasks.CountAsync(t => t.CorrelationId == correlation)).ShouldBe(0);
    }

    [Fact]
    public async Task Enqueue_DuplicateIdempotencyKey_IsRejectedByUniqueIndex()
    {
        var key = $"dup-{Guid.NewGuid()}";
        await using var db = fixture.NewDbContext();
        var writer = new OutboxWriter(db, TimeProvider.System);
        writer.Enqueue("x", new { }, null, key);
        await db.SaveChangesAsync();

        await using var db2 = fixture.NewDbContext();
        new OutboxWriter(db2, TimeProvider.System).Enqueue("x", new { }, null, key);
        await Should.ThrowAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task DispatchBatch_MarksProcessed_AndIsNotRedelivered()
    {
        var key = $"d-{Guid.NewGuid()}";
        var dispatched = new List<string>();

        await using (var seed = fixture.NewDbContext())
        {
            new OutboxWriter(seed, TimeProvider.System).Enqueue("demo", new { v = 1 }, null, key);
            await seed.SaveChangesAsync();
        }

        var dispatcher = new OutboxDispatcher(
            fixture.NewDbContext, (m, _) => { dispatched.Add(m.IdempotencyKey); return Task.CompletedTask; },
            TimeProvider.System);

        (await dispatcher.DispatchBatchAsync(10, default)).ShouldBe(1);
        (await dispatcher.DispatchBatchAsync(10, default)).ShouldBe(0);
        dispatched.ShouldBe([key]);
    }

    [Fact]
    public async Task DispatchBatch_HandlerThrows_RecordsErrorAndRetriesLater()
    {
        var key = $"e-{Guid.NewGuid()}";
        await using (var seed = fixture.NewDbContext())
        {
            new OutboxWriter(seed, TimeProvider.System).Enqueue("demo", new { }, null, key);
            await seed.SaveChangesAsync();
        }

        var failing = new OutboxDispatcher(
            fixture.NewDbContext, (_, _) => throw new InvalidOperationException("boom"), TimeProvider.System);
        await failing.DispatchBatchAsync(10, default);

        await using var read = fixture.NewDbContext();
        var message = await read.Outbox.SingleAsync(m => m.IdempotencyKey == key);
        message.ProcessedAt.ShouldBeNull();
        message.AttemptCount.ShouldBe(1);
        message.LastError.ShouldNotBeNull();
        message.LastError.ShouldContain("boom");
    }
}
