using Orchestrator.Application.Gateway;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.PullRequests;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Steps;

/// <summary>
/// Handles pull request review feedback: reconciles to find new items, triages
/// each against the confirmed plan, and routes by disposition — answer in
/// thread, correct within scope (recording a correction attempt), or return to
/// planning for a material scope change.
/// </summary>
public sealed class FeedbackStepHandler(
    IPlanningScopeStore scope,
    IPlanConfirmationStore confirmations,
    IPullRequestHost host,
    IPullRequestTrackingStore tracking,
    IToolGateway gateway,
    IAuditWriter audit,
    OwnershipDirectory ownership,
    TimeProvider clock,
    TaskCompletionOptions options) : IStepHandler
{
    public string Capability => "pull_request.feedback";

    public async Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
    {
        var task = await scope.GetTaskAsync(context.TaskId, ct);
        var envelope = await scope.GetEnvelopeAsync(context.TaskId, ct);
        var ledger = await scope.GetLedgerAsync(context.TaskId, ct);
        if (envelope is null || ledger is null)
        {
            return new StepOutcome(null, null,
                $"Task {context.TaskId} has no authorization envelope or budget ledger.");
        }

        var plan = await LatestConfirmedPlanAsync(context.TaskId, ct);
        if (plan is null)
        {
            return new StepOutcome(null, null,
                "There is no confirmed plan for this task; feedback cannot be triaged against it.");
        }

        var reviewers = await ReviewerIdsAsync(task.Id, ct);
        var reconciler = new PullRequestReconciler(
            host, tracking, clock, reviewers, options.OrchestratorIdentityId);
        var reconciliation = await reconciler.ReconcileAsync(task.Id, ct);
        if (reconciliation.NewFeedback.Count == 0)
            return new StepOutcome(null, null, null);

        var now = clock.GetUtcNow();
        var correctionNeeded = false;
        foreach (var item in reconciliation.NewFeedback)
        {
            var disposition = FeedbackTriage.Triage(item, plan);
            switch (disposition)
            {
                case FeedbackDisposition.AnswerInThread:
                    await AnswerAsync(task, item, envelope, ledger, context.StepId, ct);
                    break;
                case FeedbackDisposition.CorrectWithinScope:
                    correctionNeeded = true;
                    await MarkHandledAsync(task.Id, item, FeedbackHandling.CorrectionMade, ct);
                    break;
                case FeedbackDisposition.RequiresReplanning:
                    await MarkHandledAsync(task.Id, item, FeedbackHandling.DeferredToPlanning, ct);
                    task.Fire(TaskTrigger.MaterialScopeChange, now);
                    await scope.SaveTaskAsync(task, ct);
                    await audit.RecordAsync(new AuditEntry(
                        Actor: "orchestrator", TaskId: task.Id, Action: "feedback.replan",
                        Reason: $"Review feedback requires replanning: {item.Text}",
                        Outcome: "MaterialScopeChange", CorrelationId: context.StepId.ToString(),
                        Details: new Dictionary<string, string>()), ct);
                    return new StepOutcome(TaskTrigger.MaterialScopeChange, null, null);
            }
        }

        if (correctionNeeded)
        {
            ledger.RecordCorrectionAttempt();
            await scope.SaveLedgerAsync(ledger, ct);
            return new StepOutcome(TaskTrigger.CorrectionRequired, null, null);
        }

        return new StepOutcome(TaskTrigger.FeedbackAnswered, null, null);
    }

    private async Task AnswerAsync(
        Domain.Tasks.WorkTask task, FeedbackItem item,
        AuthorizationEnvelope envelope, Domain.Budgets.BudgetLedger ledger,
        Guid stepId, CancellationToken ct)
    {
        var (pullRequestId, threadId) = await LocateThreadAsync(task.Id, item.ThreadId, ct);
        var call = new ToolCall(
            task.Id, stepId, "orchestrator", ActionKind.PullRequestComment,
            ResourceScope.Repository(await RepositoryForAsync(task.Id, pullRequestId, ct),
                GitBranchNaming.WorkBranchFor(task.Id)),
            Guid.CreateVersion7().ToString("N"), $"pr-reply:{task.Id}:{pullRequestId}:{item.ThreadId}",
            $"Reply to review thread {item.ThreadId} on PR {pullRequestId}.",
            envelope, ledger);
        await gateway.InvokeAsync<object?>(call,
            async c =>
            {
                await host.ReplyToThreadAsync(pullRequestId, item.ThreadId,
                    $"Acknowledged: {item.Text} — recorded for the team.", c);
                return null;
            }, ct);
        await MarkHandledAsync(task.Id, item, FeedbackHandling.Answered, ct);
    }

    private async Task<(int PullRequestId, int ThreadId)> LocateThreadAsync(
        Guid taskId, int threadId, CancellationToken ct)
    {
        var trackings = await tracking.ListByTaskAsync(taskId, ct);
        foreach (var tracked in trackings)
        {
            var detail = await host.GetAsync(tracked.PullRequestId, ct);
            if (detail.Threads.Any(t => t.ThreadId == threadId))
                return (tracked.PullRequestId, threadId);
        }

        var first = trackings.FirstOrDefault()
            ?? throw new InvalidOperationException($"Task {taskId} tracks no pull requests.");
        return (first.PullRequestId, threadId);
    }

    private async Task<Guid> RepositoryForAsync(Guid taskId, int pullRequestId, CancellationToken ct)
    {
        var trackings = await tracking.ListByTaskAsync(taskId, ct);
        return trackings.FirstOrDefault(t => t.PullRequestId == pullRequestId)?.RepositoryId
            ?? trackings.FirstOrDefault()?.RepositoryId
            ?? Guid.Empty;
    }

    private async Task MarkHandledAsync(
        Guid taskId, FeedbackItem item, FeedbackHandling handling, CancellationToken ct)
    {
        // The reconciler keys seen-comments by (ThreadId, count), so marking an
        // item handled never makes it resurface as new feedback.
        var trackings = await tracking.ListByTaskAsync(taskId, ct);
        foreach (var tracked in trackings)
        {
            tracked.MarkFeedbackHandled(item.ThreadId, item.Text, handling);
            await tracking.UpsertAsync(tracked, ct);
        }
    }

    private async Task<PlanContent?> LatestConfirmedPlanAsync(Guid taskId, CancellationToken ct)
    {
        var revisions = await confirmations.ListRevisionsAsync(taskId, ct);
        return revisions
            .Where(r => r.ConfirmationStatus == ConfirmationStatus.Confirmed)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefault()?.Content;
    }

    private async Task<IReadOnlyList<string>> ReviewerIdsAsync(Guid taskId, CancellationToken ct)
    {
        var trackings = await tracking.ListByTaskAsync(taskId, ct);
        var ids = new HashSet<string>();
        foreach (var tracked in trackings)
        {
            var owners = await ownership.FindOwnersAsync(
                Domain.Identity.OwnershipSubject.Repository, tracked.RepositoryId.ToString(), ct);
            foreach (var owner in owners.Where(o => !string.IsNullOrWhiteSpace(o.AzureDevOpsId)))
                ids.Add(owner.AzureDevOpsId!);
        }

        return ids.ToList();
    }
}
