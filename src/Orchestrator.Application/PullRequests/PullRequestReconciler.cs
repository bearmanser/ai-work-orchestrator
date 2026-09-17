using Orchestrator.Application.Ports;
using Orchestrator.Domain.PullRequests;

namespace Orchestrator.Application.PullRequests;

public sealed record ReconciliationResult(
    IReadOnlyList<FeedbackItem> NewFeedback,
    bool AllRequiredApproved,
    bool HeadMoved);

public interface IPullRequestTrackingStore
{
    Task<IReadOnlyList<PullRequestTracking>> ListByTaskAsync(Guid taskId, CancellationToken ct);

    Task UpsertAsync(PullRequestTracking tracking, CancellationToken ct);

    /// <summary>Finds the tracking row (and therefore the owning task) for a pull
    /// request id, regardless of which task tracks it. Used to route an inbound PR
    /// webhook event - which only carries the Azure DevOps pull request id - back to
    /// the task that opened it.</summary>
    Task<PullRequestTracking?> FindByPullRequestIdAsync(int pullRequestId, CancellationToken ct);
}

public sealed class InMemoryPullRequestTrackingStore : IPullRequestTrackingStore
{
    private readonly Dictionary<Guid, PullRequestTracking> trackings = [];
    private readonly object gate = new();

    public Task<IReadOnlyList<PullRequestTracking>> ListByTaskAsync(Guid taskId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PullRequestTracking>>(
                trackings.Values.Where(t => t.TaskId == taskId).ToList());
        }
    }

    public Task UpsertAsync(PullRequestTracking tracking, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tracking);
        lock (gate)
        {
            trackings[tracking.Id] = tracking;
        }

        return Task.CompletedTask;
    }

    public Task<PullRequestTracking?> FindByPullRequestIdAsync(int pullRequestId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(trackings.Values
                .FirstOrDefault(t => t.PullRequestId == pullRequestId));
        }
    }
}

public sealed class PullRequestReconciler(
    IPullRequestHost host,
    IPullRequestTrackingStore store,
    TimeProvider clock,
    IReadOnlyList<string> configuredReviewerIds,
    string orchestratorIdentityId)
{
    public async Task<ReconciliationResult> ReconcileAsync(Guid taskId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var trackings = await store.ListByTaskAsync(taskId, ct);
        var newFeedback = new List<FeedbackItem>();
        var headMoved = false;

        foreach (var tracking in trackings)
        {
            var detail = await host.GetAsync(tracking.PullRequestId, ct);

            if (!string.Equals(detail.Ref.LastMergeSourceCommit, tracking.CurrentHeadCommit, StringComparison.Ordinal))
            {
                headMoved = true;
                tracking.UpdateHeadCommit(detail.Ref.LastMergeSourceCommit, now);
            }

            foreach (var thread in detail.Threads)
            {
                if (thread.Comments.Count == 0)
                {
                    continue;
                }

                var latest = thread.Comments[^1];
                if (string.Equals(latest.AuthorId, orchestratorIdentityId, StringComparison.Ordinal))
                {
                    continue;
                }

                var stored = tracking.Feedback.Count(f => f.ThreadId == thread.ThreadId);
                if (thread.Comments.Count <= stored)
                {
                    continue;
                }

                for (var i = stored; i < thread.Comments.Count; i++)
                {
                    var comment = thread.Comments[i];
                    var item = new FeedbackItem(
                        thread.ThreadId,
                        comment.AuthorId ?? string.Empty,
                        comment.Content,
                        detail.Ref.LastMergeSourceCommit,
                        FeedbackHandling.Unhandled);
                    tracking.RecordFeedback(item);
                    newFeedback.Add(item);
                }
            }

            var outcome = ApprovalRules.Evaluate(ToDetail(detail), configuredReviewerIds, orchestratorIdentityId);
            tracking.SetApproval(outcome.Approved, now);
            await store.UpsertAsync(tracking, ct);
        }

        return new ReconciliationResult(newFeedback, TaskCompletionRules.AllRequiredApproved(trackings), headMoved);
    }

    private static PullRequestApprovalDetail ToDetail(PullRequestDetail detail) =>
        new(
            detail.Reviewers.Select(r => new ApprovalVote(r.ReviewerId, r.Vote, r.IsRequired)).ToList(),
            detail.Threads.Select(t => new ApprovalThread(t.ThreadId, t.Status)).ToList(),
            detail.Policies.Select(p => new ApprovalPolicy(p.Id, p.Status)).ToList());
}
