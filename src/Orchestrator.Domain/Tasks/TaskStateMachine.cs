namespace Orchestrator.Domain.Tasks;

public static class TaskStateMachine
{
    private static readonly Dictionary<(TaskState, TaskTrigger), TaskState> Map = new()
    {
        [(TaskState.Received, TaskTrigger.PlanningStarted)] = TaskState.Planning,

        [(TaskState.Planning, TaskTrigger.BlockingQuestionRaised)] = TaskState.WaitingForHuman,
        [(TaskState.Planning, TaskTrigger.PlanPosted)] = TaskState.WaitingForPlanConfirmation,

        [(TaskState.WaitingForHuman, TaskTrigger.QuestionAnswered)] = TaskState.Planning,
        [(TaskState.WaitingForHuman, TaskTrigger.QuestionTimedOut)] = TaskState.Suspended,
        [(TaskState.Suspended, TaskTrigger.ManuallyResumed)] = TaskState.Planning,
        [(TaskState.Suspended, TaskTrigger.QuestionAnswered)] = TaskState.Planning,

        [(TaskState.WaitingForPlanConfirmation, TaskTrigger.PlanRevisionRequested)] = TaskState.Planning,
        [(TaskState.WaitingForPlanConfirmation, TaskTrigger.PlanConfirmed)] = TaskState.Ready,

        [(TaskState.Ready, TaskTrigger.WorkDispatched)] = TaskState.Running,

        [(TaskState.Running, TaskTrigger.RestrictedActionRequested)] = TaskState.WaitingForActionApproval,
        [(TaskState.Running, TaskTrigger.ImplementationFinished)] = TaskState.Verifying,
        [(TaskState.Running, TaskTrigger.BaselineDriftDetected)] = TaskState.Planning,
        [(TaskState.Running, TaskTrigger.BlockingQuestionRaised)] = TaskState.WaitingForHuman,

        [(TaskState.WaitingForActionApproval, TaskTrigger.ActionApproved)] = TaskState.Ready,
        [(TaskState.WaitingForActionApproval, TaskTrigger.ActionDenied)] = TaskState.Planning,

        [(TaskState.Verifying, TaskTrigger.CorrectionRequired)] = TaskState.Running,
        [(TaskState.Verifying, TaskTrigger.PullRequestOpened)] = TaskState.WatchingPullRequests,
        [(TaskState.Verifying, TaskTrigger.NonCodeOutcomeAccepted)] = TaskState.Completed,
        [(TaskState.Verifying, TaskTrigger.BaselineDriftDetected)] = TaskState.Planning,

        [(TaskState.WatchingPullRequests, TaskTrigger.PullRequestFeedbackReceived)] = TaskState.AddressingFeedback,
        [(TaskState.WatchingPullRequests, TaskTrigger.PullRequestOpened)] = TaskState.WatchingPullRequests,
        [(TaskState.WatchingPullRequests, TaskTrigger.AllRequiredApprovalsVerified)] = TaskState.Completed,
        [(TaskState.WatchingPullRequests, TaskTrigger.BaselineDriftDetected)] = TaskState.Planning,

        [(TaskState.AddressingFeedback, TaskTrigger.FeedbackAnswered)] = TaskState.WatchingPullRequests,
        [(TaskState.AddressingFeedback, TaskTrigger.CorrectionRequired)] = TaskState.Running,
        [(TaskState.AddressingFeedback, TaskTrigger.MaterialScopeChange)] = TaskState.Planning,
        [(TaskState.AddressingFeedback, TaskTrigger.BlockingQuestionRaised)] = TaskState.WaitingForHuman,
    };

    private static readonly HashSet<TaskState> Terminal =
        [TaskState.Completed, TaskState.Failed, TaskState.Cancelled];

    public static bool IsTerminal(TaskState state) => Terminal.Contains(state);

    public static bool TryTransition(TaskState from, TaskTrigger trigger, out TaskState to)
    {
        to = from;
        if (IsTerminal(from)) return false;

        if (trigger is TaskTrigger.Cancelled) { to = TaskState.Cancelled; return true; }
        if (trigger is TaskTrigger.UnrecoverableFailure) { to = TaskState.Failed; return true; }

        if (!Map.TryGetValue((from, trigger), out var next)) return false;
        to = next;
        return true;
    }
}
