using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Jobs;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class LeaseManagerTests(PostgresFixture fixture)
{
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));

    [Fact]
    public async Task TryAcquire_UnleasedStep_Succeeds()
    {
        var stepId = await SeedStepAsync();
        var manager = new LeaseManager(fixture.NewDbContext(), _clock);

        (await manager.TryAcquireAsync(stepId, "worker-1", TimeSpan.FromMinutes(5), default)).ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquire_AlreadyLeasedByAnother_Fails()
    {
        var stepId = await SeedStepAsync();
        await new LeaseManager(fixture.NewDbContext(), _clock)
            .TryAcquireAsync(stepId, "worker-1", TimeSpan.FromMinutes(5), default);

        (await new LeaseManager(fixture.NewDbContext(), _clock)
            .TryAcquireAsync(stepId, "worker-2", TimeSpan.FromMinutes(5), default)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryAcquire_AfterLeaseExpires_SucceedsForNewOwner()
    {
        var stepId = await SeedStepAsync();
        await new LeaseManager(fixture.NewDbContext(), _clock)
            .TryAcquireAsync(stepId, "worker-1", TimeSpan.FromMinutes(5), default);

        _clock.Advance(TimeSpan.FromMinutes(6));

        (await new LeaseManager(fixture.NewDbContext(), _clock)
            .TryAcquireAsync(stepId, "worker-2", TimeSpan.FromMinutes(5), default)).ShouldBeTrue();
    }

    [Fact]
    public async Task Heartbeat_ByLeaseHolder_ExtendsExpiry()
    {
        var stepId = await SeedStepAsync();
        var manager = new LeaseManager(fixture.NewDbContext(), _clock);
        await manager.TryAcquireAsync(stepId, "worker-1", TimeSpan.FromMinutes(5), default);

        _clock.Advance(TimeSpan.FromMinutes(4));
        (await new LeaseManager(fixture.NewDbContext(), _clock)
            .HeartbeatAsync(stepId, "worker-1", TimeSpan.FromMinutes(5), default)).ShouldBeTrue();

        _clock.Advance(TimeSpan.FromMinutes(4));
        (await new LeaseManager(fixture.NewDbContext(), _clock)
            .TryAcquireAsync(stepId, "worker-2", TimeSpan.FromMinutes(5), default)).ShouldBeFalse();
    }

    [Fact]
    public async Task Heartbeat_ByNonHolder_Fails()
    {
        var stepId = await SeedStepAsync();
        await new LeaseManager(fixture.NewDbContext(), _clock)
            .TryAcquireAsync(stepId, "worker-1", TimeSpan.FromMinutes(5), default);

        (await new LeaseManager(fixture.NewDbContext(), _clock)
            .HeartbeatAsync(stepId, "worker-2", TimeSpan.FromMinutes(5), default)).ShouldBeFalse();
    }

    private async Task<Guid> SeedStepAsync()
    {
        await using var db = fixture.NewDbContext();
        var task = new WorkTask("lease test", "U1", TaskSource.Slack, $"c-{Guid.NewGuid()}", _clock.GetUtcNow());
        var step = new Step(task.Id, "planning", dependsOnStepId: null, inputArtifactId: null, _clock.GetUtcNow());
        db.Tasks.Add(task);
        db.Steps.Add(step);
        await db.SaveChangesAsync();
        return step.Id;
    }
}
