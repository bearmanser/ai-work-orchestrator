namespace Orchestrator.Domain.PullRequests;

public sealed record ApprovalOutcome(bool Approved, string Reason);

public sealed record ApprovalVote(string ReviewerId, int Vote, bool IsRequired);

public sealed record ApprovalThread(int ThreadId, string Status);

public sealed record ApprovalPolicy(string Id, string Status);

public sealed record PullRequestApprovalDetail(
    IReadOnlyList<ApprovalVote> Votes,
    IReadOnlyList<ApprovalThread> Threads,
    IReadOnlyList<ApprovalPolicy> Policies);

public static class ApprovalRules
{
    public static ApprovalOutcome Evaluate(
        PullRequestApprovalDetail detail,
        IReadOnlyList<string> configuredReviewerIds,
        string orchestratorIdentityId)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(configuredReviewerIds);
        if (string.IsNullOrWhiteSpace(orchestratorIdentityId))
            throw new ArgumentException("Orchestrator identity is required.", nameof(orchestratorIdentityId));

        var blocking = detail.Votes.FirstOrDefault(v => v.Vote is -10 or -5);
        if (blocking is not null)
        {
            return blocking.Vote == -10
                ? new ApprovalOutcome(false, $"rejected by {blocking.ReviewerId}")
                : new ApprovalOutcome(false, $"waiting for author requested by {blocking.ReviewerId}");
        }

        var hasConfiguredApproval = detail.Votes.Any(v =>
            v.Vote is 10 or 5 &&
            configuredReviewerIds.Contains(v.ReviewerId, StringComparer.Ordinal) &&
            !string.Equals(v.ReviewerId, orchestratorIdentityId, StringComparison.Ordinal));
        if (!hasConfiguredApproval)
        {
            var orchestratorApproved = detail.Votes.Any(v =>
                v.Vote is 10 or 5 &&
                string.Equals(v.ReviewerId, orchestratorIdentityId, StringComparison.Ordinal));
            if (orchestratorApproved)
            {
                return new ApprovalOutcome(false, "the orchestrator cannot approve its own pull request");
            }

            return new ApprovalOutcome(false, "waiting for approval from a configured reviewer");
        }

        var activeCount = detail.Threads.Count(t =>
            string.Equals(t.Status, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Status, "pending", StringComparison.OrdinalIgnoreCase));
        if (activeCount > 0)
        {
            return new ApprovalOutcome(
                false,
                $"waiting for: {activeCount} active comment thread{(activeCount == 1 ? string.Empty : "s")}");
        }

        var failing = detail.Policies.FirstOrDefault(p =>
            !string.Equals(p.Status, "approved", StringComparison.OrdinalIgnoreCase));
        if (failing is not null)
        {
            return new ApprovalOutcome(false, $"failing policy: {failing.Id} is {failing.Status}");
        }

        return new ApprovalOutcome(true, "approved");
    }
}

public static class TaskCompletionRules
{
    public static bool AllRequiredApproved(IReadOnlyList<PullRequestTracking> trackings)
    {
        ArgumentNullException.ThrowIfNull(trackings);
        var required = trackings.Where(t => t.IsRequired).ToList();
        return required.Count > 0 && required.All(t => t.IsApproved);
    }
}
