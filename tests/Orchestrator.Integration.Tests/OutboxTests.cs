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
        // This assembly's PostgreSQL fixture (and its outbox_messages table) is
        // shared across every test in this class for the lifetime of the test run.
        // DispatchBatchAsync's batch scan is not scoped to "this test's own
        // messages" - it is a global SELECT ... LIMIT over whatever is currently
        // eligible. Draining any already-eligible messages first (left behind by,
        // e.g., Enqueue_DuplicateIdempotencyKey_IsRejectedByUniqueIndex's first,
        // never-dispatched insert) keeps this test's own exact-count assertions
        // deterministic regardless of test execution order.
        await DrainPendingAsync();

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
        // See DispatchBatch_MarksProcessed_AndIsNotRedelivered: drain first so this
        // test's own dispatched-count assertions cannot be inflated or masked by
        // unrelated messages left eligible by other tests in the shared fixture.
        await DrainPendingAsync();

        var key = $"e-{Guid.NewGuid()}";
        var clock = new ManualTimeProvider(Now);

        await using (var seed = fixture.NewDbContext())
        {
            new OutboxWriter(seed, clock).Enqueue("demo", new { }, null, key);
            await seed.SaveChangesAsync();
        }

        var attempts = 0;
        var handled = new List<string>();
        var dispatcher = new OutboxDispatcher(
            fixture.NewDbContext,
            (m, _) =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("boom");
                handled.Add(m.IdempotencyKey);
                return Task.CompletedTask;
            },
            clock);

        // First dispatch: the handler throws, so the message is recorded as failed
        // with a future retry time and nothing is dispatched successfully.
        (await dispatcher.DispatchBatchAsync(10, default)).ShouldBe(0);

        await using (var read = fixture.NewDbContext())
        {
            var message = await read.Outbox.SingleAsync(m => m.IdempotencyKey == key);
            message.ProcessedAt.ShouldBeNull();
            message.AttemptCount.ShouldBe(1);
            message.LastError.ShouldNotBeNull();
            message.LastError.ShouldContain("boom");
            message.NextAttemptAt.ShouldNotBeNull();
            message.NextAttemptAt!.Value.ShouldBeGreaterThan(clock.GetUtcNow());

            // Second dispatch, same clock: the message is still backing off, so the
            // dispatcher's WHERE clause must exclude it - no redelivery, no second
            // handler invocation.
            (await dispatcher.DispatchBatchAsync(10, default)).ShouldBe(0);
            attempts.ShouldBe(1);

            // Advance the clock past NextAttemptAt: the message must come back and,
            // this time, succeed - proving it was excluded rather than lost.
            clock.Advance(message.NextAttemptAt!.Value - clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        }

        (await dispatcher.DispatchBatchAsync(10, default)).ShouldBe(1);
        handled.ShouldBe([key]);

        await using var final = fixture.NewDbContext();
        (await final.Outbox.SingleAsync(m => m.IdempotencyKey == key)).ProcessedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// Drains every currently-eligible outbox message with a no-op handler. Used by
    /// tests that assert an exact dispatched count, to isolate them from unrelated
    /// messages other tests left behind in this class's shared PostgreSQL fixture.
    /// </summary>
    private async Task DrainPendingAsync()
    {
        var drain = new OutboxDispatcher(fixture.NewDbContext, (_, _) => Task.CompletedTask, TimeProvider.System);
        while (await drain.DispatchBatchAsync(1000, default) > 0)
        {
        }
    }
}
