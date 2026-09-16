namespace Orchestrator.Domain.PullRequests;

public enum FeedbackHandling
{
    Unhandled = 0,
    Answered = 1,
    CorrectionMade = 2,
    DeferredToPlanning = 3,
}

public sealed record FeedbackItem(
    int ThreadId,
    string AuthorId,
    string Text,
    string HeadCommitWhenRaised,
    FeedbackHandling Handling);
