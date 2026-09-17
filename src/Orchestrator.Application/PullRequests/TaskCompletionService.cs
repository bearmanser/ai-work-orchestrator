using Orchestrator.Application.Identity;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.PullRequests;

/// <summary>Configuration for task completion.</summary>
public sealed class TaskCompletionOptions
{
    public string OrchestratorIdentityId { get; set; } = "U-orchestrator";
    public string FallbackChannelId { get; set; } = "C-tasks";
}

public sealed record CompletionOutcome(bool Completed, string Reason);

/// <summary>
/// Completes a task only when every required PR is approved on its current
/// head: re-fetches each PR live (never trusts cache), refreshes tracking
/// heads and approval flags, cancels when a required PR was closed without
/// approval, and posts every PR link on success.
/// </summary>
public sealed class TaskCompletionService(
    IPullRequestTrackingStore tracking,
    IPullRequestHost host,
    OwnershipDirectory ownership,
    IPlanningScopeStore scope,
    IStepStore steps,
    IChatGateway chat,
    IAuditWriter audit,
    TimeProvider clock,
    TaskCompletionOptions options)
{
    public async Task<CompletionOutcome> TryCompleteAsync(Guid taskId, CancellationToken ct)
    {
        var task = await scope.GetTaskAsync(taskId, ct);
        if (TaskStateMachine.IsTerminal(task.State))
            return new CompletionOutcome(false, $"Task is already {task.State}.");
        if (task.State != TaskState.WatchingPullRequests)
        {
            return new CompletionOutcome(false,
                $"Task is {task.State}, not watching pull requests.");
        }

        var trackings = (await tracking.ListByTaskAsync(taskId, ct)).Where(t => t.IsRequired).ToList();
        if (trackings.Count == 0)
            return new CompletionOutcome(false, "Task has no required pull requests.");

        var now = clock.GetUtcNow();
        foreach (var tracked in trackings)
        {
            var detail = await host.GetAsync(tracked.PullRequestId, ct);
            if (detail.Status == PullRequestStatus.Abandoned)
            {
                task.Fire(TaskTrigger.Cancelled, now);
                await scope.SaveTaskAsync(task, ct);
                await chat.PostAsync(new ChatMessage(
                    await ChannelForAsync(taskId, ct), null, null,
                    $"Task cancelled: PR {tracked.PullRequestId} was closed without approval."), ct);
                await audit.RecordAsync(new AuditEntry(
                    Actor: "orchestrator", TaskId: taskId, Action: "task.cancelled",
                    Reason: $"PR {tracked.PullRequestId} was closed without approval.",
                    Outcome: "Cancelled", CorrelationId: taskId.ToString(),
                    Details: new Dictionary<string, string>()), ct);
                return new CompletionOutcome(false, $"PR {tracked.PullRequestId} was closed without approval.");
            }

            var head = detail.Ref.LastMergeSourceCommit;
            if (!string.Equals(head, tracked.CurrentHeadCommit, StringComparison.Ordinal))
            {
                // The code moved under a previous approval: prior votes no longer
                // bind, matching the usual "reset votes on new push" policy. The PR
                // needs re-review at the new head before it can complete anything.
                tracked.UpdateHeadCommit(head, now);
                tracked.SetApproval(false, now);
                await tracking.UpsertAsync(tracked, ct);
                continue;
            }

            var reviewers = await ReviewerIdsAsync(tracked.RepositoryId, ct);
            var outcome = ApprovalRules.Evaluate(
                new PullRequestApprovalDetail(
                    detail.Reviewers.Select(r => new ApprovalVote(r.ReviewerId, r.Vote, r.IsRequired)).ToList(),
                    detail.Threads.Select(t => new ApprovalThread(t.ThreadId, t.Status)).ToList(),
                    detail.Policies.Select(p => new ApprovalPolicy(p.Id, p.Status)).ToList()),
                reviewers,
                options.OrchestratorIdentityId);
            tracked.SetApproval(outcome.Approved, now);
            await tracking.UpsertAsync(tracked, ct);
        }

        if (!TaskCompletionRules.AllRequiredApproved(trackings))
        {
            var waiting = trackings
                .Where(t => !t.IsApproved)
                .Select(t => t.PullRequestId.ToString());
            return new CompletionOutcome(false,
                $"Waiting for approval on PR {string.Join(", PR ", waiting)}.");
        }

        task.Fire(TaskTrigger.AllRequiredApprovalsVerified, now);
        await scope.SaveTaskAsync(task, ct);

        // Memory curation (Task 42) runs as a step; it is created here but not
        // enqueued — nothing can execute it until that task lands, and an
        // undispatched Pending row is visible in admin rather than lost.
        await steps.AddAsync(new Step(taskId, "memory.curate", null, null, now), ct);

        var links = string.Join("\n", trackings.Select(t => $"- {t.Url}"));
        await chat.PostAsync(new ChatMessage(
            await ChannelForAsync(taskId, ct), null, null,
            $"All required pull requests are approved:\n{links}"), ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: "orchestrator", TaskId: taskId, Action: "task.completed",
            Reason: $"All {trackings.Count} required pull requests approved.",
            Outcome: "Completed", CorrelationId: taskId.ToString(),
            Details: new Dictionary<string, string>()), ct);

        return new CompletionOutcome(true, $"Completed with {trackings.Count} approved pull requests.");
    }

    private async Task<IReadOnlyList<string>> ReviewerIdsAsync(Guid repositoryId, CancellationToken ct)
    {
        var owners = await ownership.FindOwnersAsync(
            Domain.Identity.OwnershipSubject.Repository, repositoryId.ToString(), ct);
        return owners
            .Where(o => !string.IsNullOrWhiteSpace(o.AzureDevOpsId))
            .Select(o => o.AzureDevOpsId!)
            .ToList();
    }

    private async Task<string> ChannelForAsync(Guid taskId, CancellationToken ct)
    {
        var envelope = await scope.GetEnvelopeAsync(taskId, ct);
        return envelope?.SlackChannels.FirstOrDefault() ?? options.FallbackChannelId;
    }
}
