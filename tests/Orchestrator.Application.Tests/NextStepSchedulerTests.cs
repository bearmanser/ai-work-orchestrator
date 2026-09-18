using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

/// <summary>
/// Proves the (from-state, trigger) -&gt; capability map Task 50 introduced to fix
/// the gap Task 49 found: nothing in production created the next Step after one
/// finished, so no real task could progress past plan confirmation.
/// </summary>
public sealed class NextStepFactoryTests
{
    [Theory]
    [InlineData(TaskState.WaitingForPlanConfirmation, TaskTrigger.PlanConfirmed, "coding_agent.execute")]
    [InlineData(TaskState.WaitingForActionApproval, TaskTrigger.ActionApproved, "coding_agent.execute")]
    [InlineData(TaskState.Running, TaskTrigger.ImplementationFinished, "verification")]
    [InlineData(TaskState.Verifying, TaskTrigger.CorrectionRequired, "coding_agent.execute")]
    [InlineData(TaskState.AddressingFeedback, TaskTrigger.CorrectionRequired, "coding_agent.execute")]
    [InlineData(TaskState.Verifying, TaskTrigger.PullRequestOpened, "pull_request.create")]
    [InlineData(TaskState.WatchingPullRequests, TaskTrigger.PullRequestFeedbackReceived, "pull_request.feedback")]
    // The re-plan safety paths Task 49/50 correctly compute a Planning destination
    // for, but which the catch-all's comment never actually listed: drift
    // detected mid-flight, a restricted action denied, out-of-scope feedback, and
    // a human requesting plan changes. Each needs a fresh "planning" step just
    // like the initial confirmation flow does, or the task would sit in Planning
    // forever with nothing dispatched.
    [InlineData(TaskState.Running, TaskTrigger.BaselineDriftDetected, "planning")]
    [InlineData(TaskState.Verifying, TaskTrigger.BaselineDriftDetected, "planning")]
    [InlineData(TaskState.WatchingPullRequests, TaskTrigger.BaselineDriftDetected, "planning")]
    [InlineData(TaskState.WaitingForActionApproval, TaskTrigger.ActionDenied, "planning")]
    [InlineData(TaskState.AddressingFeedback, TaskTrigger.MaterialScopeChange, "planning")]
    [InlineData(TaskState.WaitingForPlanConfirmation, TaskTrigger.PlanRevisionRequested, "planning")]
    public void CapabilityFor_KnownTransition_ReturnsExpectedCapability(
        TaskState from, TaskTrigger trigger, string expectedCapability)
    {
        NextStepFactory.CapabilityFor(from, trigger).ShouldBe(expectedCapability);
    }

    [Theory]
    // The pull_request.create step's own outcome re-fires PullRequestOpened while
    // already in WatchingPullRequests (the state machine's self-loop): must NOT
    // create a second creation step, or every PR-creation step would spawn another
    // forever.
    [InlineData(TaskState.WatchingPullRequests, TaskTrigger.PullRequestOpened)]
    // FeedbackAnswered returns to WatchingPullRequests via reconciliation polling,
    // not a new step.
    [InlineData(TaskState.AddressingFeedback, TaskTrigger.FeedbackAnswered)]
    // TaskCompletionService already creates "memory.curate" itself when it fires
    // this trigger; the scheduler must not create a second, duplicate step.
    [InlineData(TaskState.WatchingPullRequests, TaskTrigger.AllRequiredApprovalsVerified)]
    // Idle/waiting transitions never dispatch anything.
    [InlineData(TaskState.Planning, TaskTrigger.BlockingQuestionRaised)]
    [InlineData(TaskState.WaitingForHuman, TaskTrigger.QuestionAnswered)]
    [InlineData(TaskState.Verifying, TaskTrigger.NonCodeOutcomeAccepted)]
    public void CapabilityFor_TransitionNeedingNoStep_ReturnsNull(TaskState from, TaskTrigger trigger)
    {
        NextStepFactory.CapabilityFor(from, trigger).ShouldBeNull();
    }
}

public sealed class NextStepSchedulerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T10:00:00Z");

    [Fact]
    public async Task ScheduleAsync_PlanConfirmed_CreatesCodingAgentStepAndDispatchesOutbox()
    {
        var task = NewTask();
        task.Fire(TaskTrigger.PlanningStarted, Now);
        task.Fire(TaskTrigger.PlanPosted, Now);
        var from = task.State; // WaitingForPlanConfirmation
        task.Fire(TaskTrigger.PlanConfirmed, Now);

        var steps = new RecordingStepStore();
        var outbox = new RecordingOutboxWriter();
        var scheduler = new NextStepScheduler(steps, outbox, new ManualTimeProvider(Now));

        var created = await scheduler.ScheduleAsync(task, from, TaskTrigger.PlanConfirmed, default);

        created.ShouldNotBeNull();
        created!.Capability.ShouldBe("coding_agent.execute");
        steps.Added.ShouldContain(created);
        outbox.Enqueued.Count.ShouldBe(1);
        outbox.Enqueued[0].MessageType.ShouldBe("step.dispatch");
        // Ready is transient - nothing else dispatches WorkDispatched, so the
        // scheduler itself must advance Ready -> Running or the step it just
        // created could never legally fire ImplementationFinished.
        task.State.ShouldBe(TaskState.Running);
    }

    [Fact]
    public async Task ScheduleAsync_ActionApproved_AlsoAdvancesReadyToRunning()
    {
        var task = NewTask();
        task.Fire(TaskTrigger.PlanningStarted, Now);
        task.Fire(TaskTrigger.PlanPosted, Now);
        task.Fire(TaskTrigger.PlanConfirmed, Now); // -> Ready
        task.Fire(TaskTrigger.WorkDispatched, Now); // -> Running
        task.Fire(TaskTrigger.RestrictedActionRequested, Now); // -> WaitingForActionApproval
        var from = task.State;
        task.Fire(TaskTrigger.ActionApproved, Now); // -> Ready

        var scheduler = new NextStepScheduler(
            new RecordingStepStore(), new RecordingOutboxWriter(), new ManualTimeProvider(Now));
        var created = await scheduler.ScheduleAsync(task, from, TaskTrigger.ActionApproved, default);

        created!.Capability.ShouldBe("coding_agent.execute");
        task.State.ShouldBe(TaskState.Running);
    }

    [Fact]
    public async Task ScheduleAsync_ImplementationFinished_CreatesVerificationStepWithoutExtraTransition()
    {
        var task = NewTask();
        task.Fire(TaskTrigger.PlanningStarted, Now);
        task.Fire(TaskTrigger.PlanPosted, Now);
        task.Fire(TaskTrigger.PlanConfirmed, Now);
        task.Fire(TaskTrigger.WorkDispatched, Now);
        var from = task.State; // Running
        task.Fire(TaskTrigger.ImplementationFinished, Now); // -> Verifying

        var scheduler = new NextStepScheduler(
            new RecordingStepStore(), new RecordingOutboxWriter(), new ManualTimeProvider(Now));
        var created = await scheduler.ScheduleAsync(task, from, TaskTrigger.ImplementationFinished, default);

        created!.Capability.ShouldBe("verification");
        task.State.ShouldBe(TaskState.Verifying);
    }

    [Fact]
    public async Task ScheduleAsync_TransitionWithNoMapping_CreatesNothing()
    {
        var task = NewTask();
        task.Fire(TaskTrigger.PlanningStarted, Now);
        var from = task.State;
        task.Fire(TaskTrigger.BlockingQuestionRaised, Now); // -> WaitingForHuman

        var steps = new RecordingStepStore();
        var scheduler = new NextStepScheduler(steps, new RecordingOutboxWriter(), new ManualTimeProvider(Now));

        var created = await scheduler.ScheduleAsync(task, from, TaskTrigger.BlockingQuestionRaised, default);

        created.ShouldBeNull();
        steps.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task ScheduleAsync_WatchingPullRequestsSelfLoop_DoesNotCreateASecondCreationStep()
    {
        var task = NewTask();
        task.Fire(TaskTrigger.PlanningStarted, Now);
        task.Fire(TaskTrigger.PlanPosted, Now);
        task.Fire(TaskTrigger.PlanConfirmed, Now);
        task.Fire(TaskTrigger.WorkDispatched, Now);
        task.Fire(TaskTrigger.ImplementationFinished, Now);
        task.Fire(TaskTrigger.PullRequestOpened, Now); // Verifying -> WatchingPullRequests
        var from = task.State; // WatchingPullRequests
        task.Fire(TaskTrigger.PullRequestOpened, Now); // self-loop, as the creation step's own outcome does

        var steps = new RecordingStepStore();
        var scheduler = new NextStepScheduler(steps, new RecordingOutboxWriter(), new ManualTimeProvider(Now));

        var created = await scheduler.ScheduleAsync(task, from, TaskTrigger.PullRequestOpened, default);

        created.ShouldBeNull();
        steps.Added.ShouldBeEmpty();
    }

    private static WorkTask NewTask() =>
        new("do a thing", "U1", TaskSource.Slack, $"c-{Guid.CreateVersion7()}", Now);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingStepStore : IStepStore
    {
        public List<Step> Added { get; } = [];

        public Task AddAsync(Step step, CancellationToken ct)
        {
            Added.Add(step);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Step>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Step>>(Added.Where(s => s.TaskId == taskId).ToList());
    }

    private sealed class RecordingOutboxWriter : IOutboxWriter
    {
        public List<(string MessageType, object Payload, Guid? TaskId, string IdempotencyKey)> Enqueued { get; } = [];

        public void Enqueue(string messageType, object payload, Guid? taskId, string idempotencyKey) =>
            Enqueued.Add((messageType, payload, taskId, idempotencyKey));
    }
}
