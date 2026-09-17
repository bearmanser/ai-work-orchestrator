using Orchestrator.Application.Approvals;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Plans;

public sealed record SlackInteraction(
    string ActionId,
    string RevisionHash,
    string SlackUserId,
    string? Text);

public sealed record InteractionOutcome(bool Accepted, string Reason);

/// <summary>Configuration for plan confirmation posting.</summary>
public sealed class PlanConfirmationOptions
{
    public string FallbackChannelId { get; set; } = "C-plans";
}

/// <summary>Task and plan-revision persistence for the confirmation loop.</summary>
public interface IPlanConfirmationStore
{
    Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct);
    Task SaveTaskAsync(WorkTask task, CancellationToken ct);
    Task AddRevisionAsync(PlanRevision revision, CancellationToken ct);
    Task<PlanRevision> GetRevisionAsync(Guid revisionId, CancellationToken ct);
    Task<PlanRevision?> GetLatestPendingRevisionAsync(Guid taskId, CancellationToken ct);
    Task<IReadOnlyList<PlanRevision>> ListRevisionsAsync(Guid taskId, CancellationToken ct);
    Task<PlanRevision?> FindByHashAsync(string contentHash, CancellationToken ct);
}

/// <summary>
/// Posts plan revisions for human confirmation and handles the Slack
/// interactions. Confirmation binds to the exact revision hash: a stale hash,
/// a drifted repository baseline, or an ineligible approver is refused.
/// Eligible-approver, expiry, and single-decision rules live in
/// <see cref="ApprovalService"/> and are reused here, not duplicated.
/// </summary>
public sealed class PlanConfirmationService(
    IPlanConfirmationStore store,
    ApprovalService approvals,
    OwnershipDirectory directory,
    IdentityResolver identities,
    IRepositoryProfileStore profiles,
    IChatGateway chat,
    IAuditWriter audit,
    TimeProvider clock,
    PlanConfirmationOptions options)
{
    public async Task<PlanRevision> PostForConfirmationAsync(
        Guid taskId, PlanContent content, CancellationToken ct, Guid? contextSnapshotId = null)
    {
        if (content.OpenQuestions.Count > 0)
        {
            throw new InvalidOperationException(
                "Resolve blocking questions before posting a plan for confirmation.");
        }

        var task = await store.GetTaskAsync(taskId, ct);
        var now = clock.GetUtcNow();
        var existing = await store.ListRevisionsAsync(taskId, ct);
        var revision = new PlanRevision(taskId, existing.Count + 1, content, contextSnapshotId);

        foreach (var older in existing.Where(r => r.ConfirmationStatus == ConfirmationStatus.Pending))
            older.Supersede(revision.Id);

        await store.AddRevisionAsync(revision, ct);

        task.Fire(TaskTrigger.PlanPosted, now);
        await store.SaveTaskAsync(task, ct);

        var changeTargets = content.RepositoryChanges
            .Where(r => r.Intent == ChangeIntent.Change)
            .Select(r => r.RepositoryId)
            .ToList();
        var confirmers = await directory.FindPlanConfirmersAsync(changeTargets, ct);
        await approvals.RequestAsync(
            taskId, "plan", revision.ContentHash, ActionKind.AgentInvoke, $"plan:{revision.ContentHash}",
            confirmers.Select(p => p.Id.ToString()).ToList(), now.AddDays(7), ct);

        var blocks = PlanBlockKitBuilder.Build(revision, content);
        await chat.PostBlocksAsync(
            options.FallbackChannelId, null, blocks,
            $"Plan revision {revision.RevisionNumber} needs confirmation.", ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: "orchestrator",
            TaskId: taskId,
            Action: "plan.posted",
            Reason: $"Plan revision {revision.RevisionNumber} posted for confirmation.",
            Outcome: "Success",
            CorrelationId: revision.Id.ToString(),
            Details: new Dictionary<string, string> { ["contentHash"] = revision.ContentHash }), ct);

        return revision;
    }

    public async Task<InteractionOutcome> HandleInteractionAsync(
        SlackInteraction interaction, CancellationToken ct)
    {
        var revision = await store.FindByHashAsync(interaction.RevisionHash, ct);
        if (revision is null)
        {
            return new InteractionOutcome(false,
                "The plan revision is unknown; it may belong to a superseded plan.");
        }

        var task = await store.GetTaskAsync(revision.TaskId, ct);
        return interaction.ActionId switch
        {
            "confirm_plan" => await ConfirmAsync(task, revision, interaction, ct),
            "request_plan_changes" => await RequestChangesAsync(task, revision, interaction, ct),
            "cancel_task" => await CancelAsync(task, revision, ct),
            _ => new InteractionOutcome(false, $"Unknown action '{interaction.ActionId}'."),
        };
    }

    private async Task<InteractionOutcome> ConfirmAsync(
        WorkTask task, PlanRevision revision, SlackInteraction interaction, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        if (revision.ConfirmationStatus != ConfirmationStatus.Pending)
            return new InteractionOutcome(false, "That plan revision was already decided.");

        var current = await store.GetLatestPendingRevisionAsync(task.Id, ct);
        if (current is null || current.Id != revision.Id
            || !string.Equals(interaction.RevisionHash, current.ContentHash, StringComparison.Ordinal))
        {
            return new InteractionOutcome(false,
                "That plan revision is stale; a newer revision is awaiting confirmation.");
        }

        var drifted = await FindDriftedRepositoryAsync(revision.Content, ct);
        if (drifted is not null)
        {
            return new InteractionOutcome(false,
                $"Repository '{drifted}' baseline moved since the plan was posted; ask for a revised plan.");
        }

        var person = await identities.ResolveBySlackUserAsync(interaction.SlackUserId, ct);
        if (person is null)
            return new InteractionOutcome(false, "You are not an eligible approver for this plan.");

        var approval = await FindPendingApprovalAsync(task.Id, revision.ContentHash, ct);
        if (approval is null)
            return new InteractionOutcome(false, "No pending approval exists for this plan revision.");

        var decision = await approvals.RecordDecisionAsync(
            approval.Id, person.Id.ToString(), ApprovalDecision.Approved,
            interaction.RevisionHash, now, ct);
        if (!decision.Accepted)
            return new InteractionOutcome(false, decision.Reason);

        revision.Confirm(person.Id, now);
        task.Fire(TaskTrigger.PlanConfirmed, now);
        await store.SaveTaskAsync(task, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: person.Id.ToString(),
            TaskId: task.Id,
            Action: "plan.confirmed",
            Reason: $"Plan revision {revision.RevisionNumber} confirmed.",
            Outcome: "Success",
            CorrelationId: revision.Id.ToString(),
            Details: new Dictionary<string, string> { ["contentHash"] = revision.ContentHash }), ct);

        return new InteractionOutcome(true, "Plan confirmed.");
    }

    private async Task<InteractionOutcome> RequestChangesAsync(
        WorkTask task, PlanRevision revision, SlackInteraction interaction, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var person = await identities.ResolveBySlackUserAsync(interaction.SlackUserId, ct);

        var approval = await FindPendingApprovalAsync(task.Id, revision.ContentHash, ct);
        if (approval is not null && person is not null)
        {
            await approvals.RecordDecisionAsync(
                approval.Id, person.Id.ToString(), ApprovalDecision.Rejected,
                revision.ContentHash, now, ct);
        }

        revision.RequestChanges();
        task.Fire(TaskTrigger.PlanRevisionRequested, now);
        await store.SaveTaskAsync(task, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: person?.Id.ToString() ?? $"slack:{interaction.SlackUserId}",
            TaskId: task.Id,
            Action: "plan.changes-requested",
            Reason: string.IsNullOrWhiteSpace(interaction.Text)
                ? "Plan changes requested."
                : $"Plan changes requested: {interaction.Text}",
            Outcome: "Success",
            CorrelationId: revision.Id.ToString(),
            Details: new Dictionary<string, string> { ["contentHash"] = revision.ContentHash }), ct);

        return new InteractionOutcome(true, "Changes requested; the task returned to planning.");
    }

    private async Task<InteractionOutcome> CancelAsync(
        WorkTask task, PlanRevision revision, CancellationToken ct)
    {
        task.Fire(TaskTrigger.Cancelled, clock.GetUtcNow());
        await store.SaveTaskAsync(task, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: $"slack:{revision.TaskId}",
            TaskId: task.Id,
            Action: "plan.task-cancelled",
            Reason: "Task cancelled from the plan confirmation message.",
            Outcome: "Success",
            CorrelationId: revision.Id.ToString(),
            Details: new Dictionary<string, string> { ["contentHash"] = revision.ContentHash }), ct);

        return new InteractionOutcome(true, "Task cancelled.");
    }

    private async Task<Approval?> FindPendingApprovalAsync(Guid taskId, string contentHash, CancellationToken ct)
    {
        var all = await approvals.ListAsync(ct);
        return all.FirstOrDefault(a =>
            a.TaskId == taskId
            && a.Decision == ApprovalDecision.Pending
            && string.Equals(a.SubjectHash, contentHash, StringComparison.Ordinal));
    }

    /// <summary>
    /// Recomputes the content hash against current repository baselines. A drifted
    /// baseline yields a different hash, so confirmation cannot carry over.
    /// Returns the drifted repository's name, if any.
    /// </summary>
    private async Task<string?> FindDriftedRepositoryAsync(PlanContent content, CancellationToken ct)
    {
        foreach (var change in content.RepositoryChanges)
        {
            var profile = await profiles.GetByIdAsync(change.RepositoryId, ct);
            var current = profile?.LastInspectedCommit;
            if (current is not null
                && !string.Equals(current, change.BaselineCommit, StringComparison.Ordinal))
            {
                return change.RepositoryName;
            }
        }

        return null;
    }
}
