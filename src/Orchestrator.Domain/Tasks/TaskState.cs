namespace Orchestrator.Domain.Tasks;

public enum TaskState
{
    Received,
    Planning,
    WaitingForHuman,
    WaitingForPlanConfirmation,
    Ready,
    Running,
    WaitingForActionApproval,
    Verifying,
    WatchingPullRequests,
    AddressingFeedback,
    Suspended,
    Completed,
    Failed,
    Cancelled,
}
