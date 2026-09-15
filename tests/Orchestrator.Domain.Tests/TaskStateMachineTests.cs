using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Domain.Tests;

public class TaskStateMachineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T10:00:00Z");

    [Theory]
    [InlineData(TaskState.Received, TaskTrigger.PlanningStarted, TaskState.Planning)]
    [InlineData(TaskState.Planning, TaskTrigger.BlockingQuestionRaised, TaskState.WaitingForHuman)]
    [InlineData(TaskState.WaitingForHuman, TaskTrigger.QuestionAnswered, TaskState.Planning)]
    [InlineData(TaskState.Planning, TaskTrigger.PlanPosted, TaskState.WaitingForPlanConfirmation)]
    [InlineData(TaskState.WaitingForPlanConfirmation, TaskTrigger.PlanRevisionRequested, TaskState.Planning)]
    [InlineData(TaskState.WaitingForPlanConfirmation, TaskTrigger.PlanConfirmed, TaskState.Ready)]
    [InlineData(TaskState.Ready, TaskTrigger.WorkDispatched, TaskState.Running)]
    [InlineData(TaskState.Running, TaskTrigger.RestrictedActionRequested, TaskState.WaitingForActionApproval)]
    [InlineData(TaskState.WaitingForActionApproval, TaskTrigger.ActionApproved, TaskState.Ready)]
    [InlineData(TaskState.Running, TaskTrigger.ImplementationFinished, TaskState.Verifying)]
    [InlineData(TaskState.Verifying, TaskTrigger.CorrectionRequired, TaskState.Running)]
    [InlineData(TaskState.Verifying, TaskTrigger.PullRequestOpened, TaskState.WatchingPullRequests)]
    [InlineData(TaskState.WatchingPullRequests, TaskTrigger.PullRequestFeedbackReceived, TaskState.AddressingFeedback)]
    [InlineData(TaskState.AddressingFeedback, TaskTrigger.FeedbackAnswered, TaskState.WatchingPullRequests)]
    [InlineData(TaskState.AddressingFeedback, TaskTrigger.CorrectionRequired, TaskState.Running)]
    [InlineData(TaskState.AddressingFeedback, TaskTrigger.MaterialScopeChange, TaskState.Planning)]
    [InlineData(TaskState.WatchingPullRequests, TaskTrigger.AllRequiredApprovalsVerified, TaskState.Completed)]
    public void TryTransition_AllowedSpecTransition_Succeeds(TaskState from, TaskTrigger trigger, TaskState expected)
    {
        TaskStateMachine.TryTransition(from, trigger, out var to).ShouldBeTrue();
        to.ShouldBe(expected);
    }

    // The four transitions PROJECT_PLAN.md section 5 omits.
    [Theory]
    [InlineData(TaskState.WaitingForActionApproval, TaskTrigger.ActionDenied, TaskState.Planning)]
    [InlineData(TaskState.WaitingForHuman, TaskTrigger.QuestionTimedOut, TaskState.Suspended)]
    [InlineData(TaskState.Suspended, TaskTrigger.ManuallyResumed, TaskState.Planning)]
    [InlineData(TaskState.Verifying, TaskTrigger.NonCodeOutcomeAccepted, TaskState.Completed)]
    [InlineData(TaskState.Running, TaskTrigger.BaselineDriftDetected, TaskState.Planning)]
    [InlineData(TaskState.Verifying, TaskTrigger.BaselineDriftDetected, TaskState.Planning)]
    [InlineData(TaskState.WatchingPullRequests, TaskTrigger.BaselineDriftDetected, TaskState.Planning)]
    public void TryTransition_GapFillingTransition_Succeeds(TaskState from, TaskTrigger trigger, TaskState expected)
    {
        TaskStateMachine.TryTransition(from, trigger, out var to).ShouldBeTrue();
        to.ShouldBe(expected);
    }

    [Theory]
    [InlineData(TaskState.Received, TaskTrigger.PlanConfirmed)]
    [InlineData(TaskState.Planning, TaskTrigger.WorkDispatched)]
    [InlineData(TaskState.WaitingForPlanConfirmation, TaskTrigger.WorkDispatched)]
    [InlineData(TaskState.Completed, TaskTrigger.PlanningStarted)]
    [InlineData(TaskState.Cancelled, TaskTrigger.WorkDispatched)]
    public void TryTransition_DisallowedTransition_Fails(TaskState from, TaskTrigger trigger)
    {
        TaskStateMachine.TryTransition(from, trigger, out _).ShouldBeFalse();
    }

    [Fact]
    public void ImplementationCannotStart_WithoutPassingThroughConfirmation()
    {
        // The only path into Running is Ready -> WorkDispatched, and the only path
        // into Ready is a confirmed plan or an approved restricted action.
        var into = Enum.GetValues<TaskState>()
            .SelectMany(s => Enum.GetValues<TaskTrigger>().Select(t => (s, t)))
            .Where(x => TaskStateMachine.TryTransition(x.s, x.t, out var to) && to == TaskState.Ready)
            .Select(x => x.t)
            .Distinct()
            .ToArray();

        into.ShouldBe([TaskTrigger.PlanConfirmed, TaskTrigger.ActionApproved], ignoreOrder: true);
    }

    [Fact]
    public void AnyActiveState_CanBeCancelledOrFailed()
    {
        foreach (var state in Enum.GetValues<TaskState>().Where(s => !TaskStateMachine.IsTerminal(s)))
        {
            TaskStateMachine.TryTransition(state, TaskTrigger.Cancelled, out var c).ShouldBeTrue();
            c.ShouldBe(TaskState.Cancelled);
            TaskStateMachine.TryTransition(state, TaskTrigger.UnrecoverableFailure, out var f).ShouldBeTrue();
            f.ShouldBe(TaskState.Failed);
        }
    }

    [Fact]
    public void Fire_DisallowedTrigger_ThrowsAndLeavesStateUnchanged()
    {
        var task = new WorkTask("x", "U1", TaskSource.Slack, "c1", Now);
        Should.Throw<InvalidTransitionException>(() => task.Fire(TaskTrigger.PlanConfirmed, Now));
        task.State.ShouldBe(TaskState.Received);
    }
}
