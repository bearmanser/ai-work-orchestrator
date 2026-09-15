using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class WorkTaskPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SaveTask_RoundTrips_WithUtcTimestampsAndVersion()
    {
        var created = new WorkTask(
            objective: "Add ordering endpoint",
            requesterId: "U123",
            source: TaskSource.Slack,
            correlationId: "slack:C1:1737000000.000100",
            createdAt: DateTimeOffset.Parse("2026-09-15T08:00:00Z"));

        await using (var db = fixture.NewDbContext())
        {
            db.Tasks.Add(created);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.NewDbContext();
        var loaded = await read.Tasks.SingleAsync(t => t.Id == created.Id);

        loaded.Objective.ShouldBe("Add ordering endpoint");
        loaded.State.ShouldBe(TaskState.Received);
        loaded.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        loaded.Version.ShouldNotBe(0u);
    }

    [Fact]
    public async Task ConcurrentUpdate_LosingWriter_ThrowsConcurrencyException()
    {
        var task = new WorkTask("x", "U1", TaskSource.Slack, "c1",
            DateTimeOffset.Parse("2026-09-15T08:00:00Z"));
        await using (var seed = fixture.NewDbContext())
        {
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync();
        }

        await using var first = fixture.NewDbContext();
        await using var second = fixture.NewDbContext();
        var a = await first.Tasks.SingleAsync(t => t.Id == task.Id);
        var b = await second.Tasks.SingleAsync(t => t.Id == task.Id);

        a.Retitle("first", DateTimeOffset.Parse("2026-09-15T09:00:00Z"));
        await first.SaveChangesAsync();

        b.Retitle("second", DateTimeOffset.Parse("2026-09-15T09:00:01Z"));
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }
}
