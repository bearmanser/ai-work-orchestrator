namespace Orchestrator.Domain.Tasks;

public enum TaskTrigger
{
    PlanningStarted,
    BlockingQuestionRaised,
    QuestionAnswered,
    QuestionTimedOut,
    ManuallyResumed,
    PlanPosted,
    PlanRevisionRequested,
    PlanConfirmed,
    WorkDispatched,
    RestrictedActionRequested,
    ActionApproved,
    ActionDenied,
    ImplementationFinished,
    CorrectionRequired,
    PullRequestOpened,
    PullRequestFeedbackReceived,
    FeedbackAnswered,
    MaterialScopeChange,
    BaselineDriftDetected,
    AllRequiredApprovalsVerified,
    NonCodeOutcomeAccepted,
    UnrecoverableFailure,
    Cancelled,
}
