namespace Orchestrator.Application.Ports;

/// <summary>Input for creating a pull request on a hosted repository.</summary>
public sealed record CreatePullRequest(
    string Organization,
    string Project,
    string RepositoryName,
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Description,
    IReadOnlyList<string> RequiredReviewerIds);

/// <summary>Lightweight reference to a pull request.</summary>
public sealed record PullRequestRef(
    int Id,
    string Url,
    string SourceBranch,
    string TargetBranch,
    string LastMergeSourceCommit);

/// <summary>Status of a pull request.</summary>
public enum PullRequestStatus
{
    Unknown = 0,
    Active = 1,
    Completed = 2,
    Abandoned = 3,
}

/// <summary>One reviewer's vote on a pull request.</summary>
public sealed record ReviewerVote(
    string ReviewerId,
    string DisplayName,
    int Vote,
    bool IsRequired);

/// <summary>One comment inside a thread.</summary>
public sealed record Comment(string Content, string? AuthorId);

/// <summary>One discussion thread on a pull request.</summary>
public sealed record CommentThread(
    int ThreadId,
    string Status,
    IReadOnlyList<Comment> Comments,
    string? FilePath);

/// <summary>One policy evaluation attached to a pull request.</summary>
public sealed record PolicyEvaluation(string Id, string Status, string? DisplayName);

/// <summary>Full pull request detail: ref, status, votes, threads and policies.</summary>
public sealed record PullRequestDetail(
    PullRequestRef Ref,
    PullRequestStatus Status,
    IReadOnlyList<ReviewerVote> Reviewers,
    IReadOnlyList<CommentThread> Threads,
    IReadOnlyList<PolicyEvaluation> Policies);

/// <summary>
/// Host operations for pull requests. Callers route through the tool gateway;
/// this port stays a plain client so the gateway can wrap it later.
/// </summary>
public interface IPullRequestHost
{
    Task<PullRequestRef> CreateAsync(CreatePullRequest request, CancellationToken ct);

    Task<PullRequestRef?> FindOpenBySourceBranchAsync(
        string organization,
        string project,
        string repositoryName,
        string sourceBranch,
        CancellationToken ct);

    Task<PullRequestDetail> GetAsync(int pullRequestId, CancellationToken ct);

    Task ReplyToThreadAsync(int pullRequestId, int threadId, string text, CancellationToken ct);

    Task UpdateDescriptionAsync(int pullRequestId, string description, CancellationToken ct);
}
