using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Operations;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Operations;

/// <summary>
/// Operational kill-switches backed by the `operational_flags` table. Tool-call
/// outcomes (the Unknown/reconcile gate) live in <see cref="IToolCallStore"/>, not
/// here - that is the single store the <see cref="Gateway.ToolGateway"/> and the
/// retry/reconcile paths both read and write, so there is exactly one place that
/// data can disagree with itself.
/// </summary>
public sealed class EfOperationalControls(
    OrchestratorDbContext db,
    IAuditWriter audit,
    IToolCallStore toolCallStore,
    OperationReconciliationService reconciliation,
    TimeProvider clock)
    : IOperationalControls
{
    private const string IntakePausedKey = "intake.paused";

    public async Task PauseIntakeAsync(string actor, CancellationToken ct) =>
        await SetAsync(IntakePausedKey, true, actor, "operations.pause-intake",
            "Task intake paused.", ct);

    public async Task ResumeIntakeAsync(string actor, CancellationToken ct) =>
        await SetAsync(IntakePausedKey, false, actor, "operations.resume-intake",
            "Task intake resumed.", ct);

    public async Task<bool> IsIntakePausedAsync(CancellationToken ct) =>
        await db.OperationalFlags
            .Where(f => f.Key == IntakePausedKey)
            .Select(f => f.Value)
            .SingleOrDefaultAsync(ct);

    public async Task DisableIntegrationAsync(string name, string actor, CancellationToken ct) =>
        await SetAsync($"integration.{name}.disabled", true, actor,
            $"operations.integrations.{name}.disable",
            $"Integration '{name}' disabled.", ct);

    public async Task EnableIntegrationAsync(string name, string actor, CancellationToken ct) =>
        await SetAsync($"integration.{name}.disabled", false, actor,
            $"operations.integrations.{name}.enable",
            $"Integration '{name}' enabled.", ct);

    public async Task<bool> IsIntegrationDisabledAsync(string name, CancellationToken ct) =>
        await db.OperationalFlags
            .Where(f => f.Key == $"integration.{name}.disabled")
            .Select(f => f.Value)
            .SingleOrDefaultAsync(ct);

    public async Task<bool> CancelTaskAsync(Guid taskId, string actor, CancellationToken ct)
    {
        var task = await db.Tasks.SingleOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new KeyNotFoundException($"Task {taskId} not found.");

        if (TaskStateMachine.IsTerminal(task.State))
            return false;

        // Cancellation never undoes an already-published effect: it does not close a
        // PR or revert a note. Capture, before the state changes underneath it,
        // whether an external effect (an open PR) is what the caller needs to be
        // told remains open.
        var hadOpenPullRequest = task.State is TaskState.WatchingPullRequests or TaskState.AddressingFeedback;

        task.Fire(TaskTrigger.Cancelled, clock.GetUtcNow());

        // Stop pending/leased work; evidence already produced (artifacts, PRs) is
        // retained untouched.
        var steps = await db.Steps
            .Where(s => s.TaskId == taskId && (s.Status == StepStatus.Pending || s.Status == StepStatus.Leased))
            .ToListAsync(ct);
        foreach (var step in steps)
            step.Cancel();

        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: taskId,
            Action: "operations.task.cancel",
            Reason: $"Task {taskId} cancelled by administrator.",
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string> { ["taskId"] = taskId.ToString() }), ct);

        return hadOpenPullRequest;
    }

    public async Task RevokeAuthorizationAsync(Guid taskId, string actor, CancellationToken ct)
    {
        var envelope = await db.AuthorizationEnvelopes.SingleOrDefaultAsync(e => e.TaskId == taskId, ct);
        if (envelope is null)
            throw new KeyNotFoundException($"Authorization envelope for task {taskId} not found.");

        envelope.Revoke(clock.GetUtcNow(), "Revoked by administrator.");
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: taskId,
            Action: "operations.task.revoke-authorization",
            Reason: $"Authorization for task {taskId} revoked by administrator.",
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string> { ["taskId"] = taskId.ToString() }), ct);
    }

    public async Task RetryStepAsync(Guid taskId, Guid stepId, string actor, CancellationToken ct)
    {
        var step = await db.Steps.SingleOrDefaultAsync(s => s.Id == stepId && s.TaskId == taskId, ct);
        if (step is null)
            throw new KeyNotFoundException($"Step {stepId} not found.");

        // Defense in depth: OperationalControlService already gates on this before
        // calling us, but this type is public and must not be safe to misuse by
        // calling it directly.
        if (await toolCallStore.HasUnknownOutcomesAsync(taskId, ct))
            throw new InvalidOperationException($"Task {taskId} has unknown tool call outcomes; reconcile first.");

        step.RetryReset(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: taskId,
            Action: "operations.task.step.retry",
            Reason: $"Step {stepId} of task {taskId} reset for retry (attempt {step.AttemptCount}).",
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string>
            {
                ["taskId"] = taskId.ToString(),
                ["stepId"] = stepId.ToString(),
                ["attemptCount"] = step.AttemptCount.ToString(),
            }), ct);
    }

    public async Task<ReconciliationSummary> ReconcileTaskAsync(Guid taskId, string actor, CancellationToken ct)
    {
        var summary = await reconciliation.ReconcileAsync(taskId, ct);

        var reason = summary.FullyReconciled
            ? $"Reconciled {summary.Resolved} succeeded and {summary.Failed} failed " +
              $"tool call outcome(s) for task {taskId}."
            : $"Reconciled {summary.Resolved} succeeded and {summary.Failed} failed, but could not reconcile " +
              $"{summary.StillUnknown} operation(s) for task {taskId} - no reconciler registered for: " +
              $"{string.Join(", ", summary.UnresolvedTargets)}.";

        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: taskId,
            Action: "operations.task.reconcile",
            Reason: reason,
            Outcome: summary.FullyReconciled ? "Success" : "Partial",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string>
            {
                ["taskId"] = taskId.ToString(),
                ["resolved"] = summary.Resolved.ToString(),
                ["failed"] = summary.Failed.ToString(),
                ["stillUnknown"] = summary.StillUnknown.ToString(),
            }), ct);

        return summary;
    }

    private async Task SetAsync(
        string key, bool value, string actor, string action, string reason, CancellationToken ct)
    {
        var flag = await db.OperationalFlags.SingleOrDefaultAsync(f => f.Key == key, ct);
        if (flag is null)
        {
            flag = new OperationalFlag(key, value, clock.GetUtcNow());
            db.OperationalFlags.Add(flag);
        }
        else
        {
            flag.Set(value, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: null,
            Action: action,
            Reason: reason,
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string> { ["key"] = key }), ct);
    }
}
