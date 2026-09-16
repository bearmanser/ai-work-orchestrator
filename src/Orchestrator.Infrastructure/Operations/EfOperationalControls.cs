using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Operations;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Operations;

/// <summary>Operational kill-switches backed by the `operational_flags` table.</summary>
public sealed class EfOperationalControls(
    OrchestratorDbContext db, IAuditWriter audit, TimeProvider clock) : IOperationalControls
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

    public async Task CancelTaskAsync(Guid taskId, string actor, CancellationToken ct)
    {
        var task = await db.Tasks.SingleOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
            throw new KeyNotFoundException($"Task {taskId} not found.");

        if (TaskStateMachine.IsTerminal(task.State))
            return;

        task.Fire(TaskTrigger.Cancelled, clock.GetUtcNow());

        // Cancel pending/leased steps
        var steps = await db.Steps
            .Where(s => s.TaskId == taskId && (s.Status == StepStatus.Pending || s.Status == StepStatus.Leased))
            .ToListAsync(ct);
        foreach (var step in steps)
            step.Cancel();

        await db.SaveChangesAsync(ct);

        // Request job cancellation
        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: taskId,
            Action: "operations.task.cancel",
            Reason: $"Task {taskId} cancelled by administrator.",
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string> { ["taskId"] = taskId.ToString() }), ct);
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

        // Check for unknown tool call outcomes
        var hasUnknownOutcome = await db.ToolCallRecords
            .AnyAsync(r => r.TaskId == taskId && r.Outcome == ToolCallOutcome.Unknown, ct);

        if (hasUnknownOutcome)
            throw new InvalidOperationException($"Task {taskId} has unknown tool call outcomes; reconcile first.");

        // Reset step to pending for retry - recreate step with pending status
        var newStep = new Step(taskId, step.Capability, step.DependsOnStepId, step.InputArtifactId, clock.GetUtcNow());
        newStep.AttemptCount = step.AttemptCount + 1;
        db.Steps.Add(newStep);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: taskId,
            Action: "operations.task.step.retry",
            Reason: $"Step {stepId} of task {taskId} queued for retry (new step {newStep.Id}).",
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string>
            {
                ["taskId"] = taskId.ToString(),
                ["oldStepId"] = stepId.ToString(),
                ["newStepId"] = newStep.Id.ToString()
            }), ct);
    }

    public async Task ReconcileTaskAsync(Guid taskId, string actor, CancellationToken ct)
    {
        // Mark all unknown tool calls as succeeded
        var unknownCalls = await db.ToolCallRecords
            .Where(r => r.TaskId == taskId && r.Outcome == ToolCallOutcome.Unknown)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        foreach (var call in unknownCalls)
        {
            call.Outcome = ToolCallOutcome.Succeeded;
            call.CompletedAt = now;
        }

        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: taskId,
            Action: "operations.task.reconcile",
            Reason: $"Reconciled {unknownCalls.Count} unknown tool call outcome(s) for task {taskId}.",
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string>
            {
                ["taskId"] = taskId.ToString(),
                ["reconciledCount"] = unknownCalls.Count.ToString()
            }), ct);
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
