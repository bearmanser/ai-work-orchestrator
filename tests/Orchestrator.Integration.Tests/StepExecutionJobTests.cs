using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Jobs;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class StepExecutionJobTests(PostgresFixture fixture)
{
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-15T13:00:00Z"));

    [Fact]
    public async Task RunAsync_HandlerSucceeds_MarksStepSucceededWithOutputArtifact()
    {
        var (_, stepId) = await SeedTaskAndStepAsync("planning");
        var handler = new StubHandler("planning", new StepOutcome(null, "artifact-42", null));
        var job = new StepExecutionJob(
            fixture.NewDbContext(), new StepDispatcher([handler]), new LeaseManager(fixture.NewDbContext(), _clock), _clock);

        await job.RunAsync(stepId, default);

        var step = await LoadStepAsync(stepId);
        step.Status.ShouldBe(StepStatus.Succeeded);
        step.OutputArtifactId.ShouldBe("artifact-42");
        handler.Invocations.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_HandlerReturnsFailure_MarksStepFailedAndIncrementsAttemptCount()
    {
        var (_, stepId) = await SeedTaskAndStepAsync("planning");
        var handler = new StubHandler("planning", new StepOutcome(null, null, "boom"));
        var job = new StepExecutionJob(
            fixture.NewDbContext(), new StepDispatcher([handler]), new LeaseManager(fixture.NewDbContext(), _clock), _clock);

        await job.RunAsync(stepId, default);

        var step = await LoadStepAsync(stepId);
        step.Status.ShouldBe(StepStatus.Failed);
        step.AttemptCount.ShouldBe(1);
        step.FailureReason.ShouldBe("boom");
    }

    [Fact]
    public async Task RunAsync_HandlerReturnsTrigger_FiresTaskTransition()
    {
        var (taskId, stepId) = await SeedTaskAndStepAsync("planning");
        var handler = new StubHandler("planning", new StepOutcome(TaskTrigger.PlanningStarted, "artifact-1", null));
        var job = new StepExecutionJob(
            fixture.NewDbContext(), new StepDispatcher([handler]), new LeaseManager(fixture.NewDbContext(), _clock), _clock);

        await job.RunAsync(stepId, default);

        await using var db = fixture.NewDbContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId);
        task.State.ShouldBe(TaskState.Planning);
    }

    [Fact]
    public async Task RunAsync_LeaseHeldByAnotherLiveOwner_DoesNotDispatchHandler()
    {
        var (_, stepId) = await SeedTaskAndStepAsync("planning");

        // Simulate another worker already holding a live (non-expired) lease on this
        // step - the exact scenario the lease exists to prevent two workers from
        // both dispatching the same step.
        (await new LeaseManager(fixture.NewDbContext(), _clock)
            .TryAcquireAsync(stepId, "other-worker", TimeSpan.FromMinutes(5), default)).ShouldBeTrue();

        var handler = new StubHandler("planning", new StepOutcome(null, "artifact-1", null));
        var job = new StepExecutionJob(
            fixture.NewDbContext(), new StepDispatcher([handler]), new LeaseManager(fixture.NewDbContext(), _clock), _clock);

        await job.RunAsync(stepId, default);

        handler.Invocations.ShouldBe(0);
        var step = await LoadStepAsync(stepId);
        step.Status.ShouldBe(StepStatus.Leased);
        step.LeaseOwner.ShouldBe("other-worker");
    }

    [Fact]
    public async Task RunAsync_UnknownCapability_MarksStepFailedInsteadOfThrowing()
    {
        var (_, stepId) = await SeedTaskAndStepAsync("no-such-capability");
        var job = new StepExecutionJob(
            fixture.NewDbContext(), new StepDispatcher([]), new LeaseManager(fixture.NewDbContext(), _clock), _clock);

        await Should.NotThrowAsync(() => job.RunAsync(stepId, default));

        var step = await LoadStepAsync(stepId);
        step.Status.ShouldBe(StepStatus.Failed);
        step.FailureReason.ShouldNotBeNull();
        step.FailureReason.ShouldContain("No handler");
    }

    private async Task<Step> LoadStepAsync(Guid stepId)
    {
        await using var db = fixture.NewDbContext();
        return await db.Steps.SingleAsync(s => s.Id == stepId);
    }

    private async Task<(Guid TaskId, Guid StepId)> SeedTaskAndStepAsync(string capability)
    {
        await using var db = fixture.NewDbContext();
        var task = new WorkTask("step exec test", "U1", TaskSource.Slack, $"c-{Guid.NewGuid()}", _clock.GetUtcNow());
        var step = new Step(task.Id, capability, dependsOnStepId: null, inputArtifactId: null, _clock.GetUtcNow());
        db.Tasks.Add(task);
        db.Steps.Add(step);
        await db.SaveChangesAsync();
        return (task.Id, step.Id);
    }

    private sealed class StubHandler(string capability, StepOutcome outcome) : IStepHandler
    {
        public string Capability => capability;
        public int Invocations { get; private set; }

        public Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(outcome);
        }
    }
}
