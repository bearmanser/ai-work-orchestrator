using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Jobs;
using Orchestrator.Infrastructure.Outbox;
using Orchestrator.Infrastructure.Persistence;
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
        var job = BuildJob(fixture.NewDbContext(), new StepDispatcher([handler]));

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
        var job = BuildJob(fixture.NewDbContext(), new StepDispatcher([handler]));

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
        var job = BuildJob(fixture.NewDbContext(), new StepDispatcher([handler]));

        await job.RunAsync(stepId, default);

        await using var db = fixture.NewDbContext();
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId);
        task.State.ShouldBe(TaskState.Planning);
    }

    [Fact]
    public async Task RunAsync_TriggerWithAMappedNextStep_CreatesAndEnqueuesIt()
    {
        // Proves the fix Task 49 found missing: nothing in production created the
        // next Step after one finished, so no real task could progress past plan
        // confirmation. Seeds a task already at WaitingForPlanConfirmation with a
        // step whose handler fires PlanConfirmed - exactly the transition
        // NextStepFactory maps to a "coding_agent.execute" step - and asserts that
        // step, and its "step.dispatch" outbox message, both now exist for real,
        // with no test-side step seeding.
        Guid taskId;
        await using (var seedDb = fixture.NewDbContext())
        {
            var task = new WorkTask("e2e chain test", "U1", TaskSource.Slack, $"c-{Guid.NewGuid()}", _clock.GetUtcNow());
            task.Fire(TaskTrigger.PlanningStarted, _clock.GetUtcNow());
            task.Fire(TaskTrigger.PlanPosted, _clock.GetUtcNow());
            seedDb.Tasks.Add(task);
            var step = new Step(task.Id, "planning", dependsOnStepId: null, inputArtifactId: null, _clock.GetUtcNow());
            seedDb.Steps.Add(step);
            await seedDb.SaveChangesAsync();
            taskId = task.Id;
        }

        var stepId = await StepIdForAsync(taskId);
        var handler = new StubHandler("planning", new StepOutcome(TaskTrigger.PlanConfirmed, null, null));
        var job = BuildJob(fixture.NewDbContext(), new StepDispatcher([handler]));

        await job.RunAsync(stepId, default);

        await using var db = fixture.NewDbContext();
        var task2 = await db.Tasks.SingleAsync(t => t.Id == taskId);
        // Ready is transient: NextStepScheduler itself fires WorkDispatched, since
        // nothing else in production does.
        task2.State.ShouldBe(TaskState.Running);

        var nextStep = await db.Steps.SingleAsync(s => s.TaskId == taskId && s.Capability == "coding_agent.execute");
        nextStep.Status.ShouldBe(StepStatus.Pending);

        var dispatchMessages = await db.Outbox.Where(m => m.MessageType == "step.dispatch").ToListAsync();
        var outboxMessage = dispatchMessages.Single(m => m.PayloadJson.Contains(nextStep.Id.ToString()));
        outboxMessage.ProcessedAt.ShouldBeNull();
    }

    private async Task<Guid> StepIdForAsync(Guid taskId)
    {
        await using var db = fixture.NewDbContext();
        return await db.Steps.Where(s => s.TaskId == taskId).Select(s => s.Id).SingleAsync();
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
        var job = BuildJob(fixture.NewDbContext(), new StepDispatcher([handler]));

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
        var job = BuildJob(fixture.NewDbContext(), new StepDispatcher([]));

        await Should.NotThrowAsync(() => job.RunAsync(stepId, default));

        var step = await LoadStepAsync(stepId);
        step.Status.ShouldBe(StepStatus.Failed);
        step.FailureReason.ShouldNotBeNull();
        step.FailureReason.ShouldContain("No handler");
    }

    private StepExecutionJob BuildJob(
        OrchestratorDbContext db, StepDispatcher dispatcher) =>
        new(db, dispatcher, new LeaseManager(fixture.NewDbContext(), _clock),
            new NextStepScheduler(new EfStepStore(db), new OutboxWriter(db, _clock), _clock), _clock);

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
