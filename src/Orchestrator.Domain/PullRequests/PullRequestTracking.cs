namespace Orchestrator.Domain.PullRequests;

public enum PullRequestTrackingStatus
{
    Active = 0,
    Completed = 1,
    Abandoned = 2,
}

public sealed class PullRequestTracking
{
    private readonly List<FeedbackItem> feedback = [];

    private PullRequestTracking() { } // EF

    public PullRequestTracking(
        Guid taskId,
        Guid repositoryId,
        int pullRequestId,
        string url,
        string currentHeadCommit,
        bool isRequired,
        DateTimeOffset lastReconciledAt)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Url is required.", nameof(url));
        ArgumentNullException.ThrowIfNull(currentHeadCommit);
        if (pullRequestId <= 0)
            throw new ArgumentOutOfRangeException(nameof(pullRequestId), "Pull request id must be positive.");

        Id = Guid.CreateVersion7();
        TaskId = taskId;
        RepositoryId = repositoryId;
        PullRequestId = pullRequestId;
        Url = url;
        CurrentHeadCommit = currentHeadCommit;
        IsRequired = isRequired;
        Status = PullRequestTrackingStatus.Active;
        LastReconciledAt = lastReconciledAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid RepositoryId { get; private set; }
    public int PullRequestId { get; private set; }
    public string Url { get; private set; } = null!;
    public string CurrentHeadCommit { get; private set; } = null!;
    public bool IsRequired { get; private set; }
    public PullRequestTrackingStatus Status { get; private set; }
    public IReadOnlyList<FeedbackItem> Feedback => feedback;
    public DateTimeOffset LastReconciledAt { get; private set; }
    public bool IsApproved { get; private set; }

    public void RecordFeedback(FeedbackItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        feedback.Add(item);
    }

    public void MarkFeedbackHandled(int threadId, string text, FeedbackHandling handling)
    {
        var index = feedback.FindIndex(f =>
            f.ThreadId == threadId && string.Equals(f.Text, text, StringComparison.Ordinal));
        if (index >= 0)
            feedback[index] = feedback[index] with { Handling = handling };
    }

    public void UpdateHeadCommit(string headCommit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headCommit);
        CurrentHeadCommit = headCommit;
        LastReconciledAt = now.ToUniversalTime();
    }

    public void SetApproval(bool approved, DateTimeOffset now)
    {
        IsApproved = approved;
        LastReconciledAt = now.ToUniversalTime();
    }
}
