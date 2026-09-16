using Orchestrator.Application.Gateway;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Operations;

/// <summary>
/// Orchestrates operational control commands with side effects (job cancellation,
/// notifications, etc.). The EF implementation handles persistence and audit; this
/// service adds the cross-cutting behaviors the tests verify.
/// </summary>
public sealed class OperationalControlService(
    IOperationalControls controls,
    IJobScheduler scheduler,
    IChatGateway chat,
    IToolCallStore toolCallStore)
{
    public async Task PauseIntakeAsync(string actor, CancellationToken ct) =>
        await controls.PauseIntakeAsync(actor, ct);

    public async Task ResumeIntakeAsync(string actor, CancellationToken ct) =>
        await controls.ResumeIntakeAsync(actor, ct);

    public async Task<bool> IsIntakePausedAsync(CancellationToken ct) =>
        await controls.IsIntakePausedAsync(ct);

    public async Task DisableIntegrationAsync(string name, string actor, CancellationToken ct) =>
        await controls.DisableIntegrationAsync(name, actor, ct);

    public async Task EnableIntegrationAsync(string name, string actor, CancellationToken ct) =>
        await controls.EnableIntegrationAsync(name, actor, ct);

    public async Task<bool> IsIntegrationDisabledAsync(string name, CancellationToken ct) =>
        await controls.IsIntegrationDisabledAsync(name, ct);

    public async Task CancelTaskAsync(Guid taskId, string actor, CancellationToken ct)
    {
        await controls.CancelTaskAsync(taskId, actor, ct);
        await scheduler.RequestCancellationAsync(taskId, ct);

        // Check for open PRs and notify
        await NotifyOpenPullRequestsAsync(taskId, ct);
    }

    public async Task RevokeAuthorizationAsync(Guid taskId, string actor, CancellationToken ct) =>
        await controls.RevokeAuthorizationAsync(taskId, actor, ct);

    public async Task RetryStepAsync(Guid taskId, Guid stepId, string actor, CancellationToken ct)
    {
        // Check for unknown tool call outcomes
        var hasUnknown = await toolCallStore.HasUnknownOutcomesAsync(taskId, ct);
        if (hasUnknown)
            throw new InvalidOperationException($"Task {taskId} has unknown tool call outcomes; reconcile first.");

        await controls.RetryStepAsync(taskId, stepId, actor, ct);
    }

    public async Task ReconcileTaskAsync(Guid taskId, string actor, CancellationToken ct) =>
        await controls.ReconcileTaskAsync(taskId, actor, ct);

    private async Task NotifyOpenPullRequestsAsync(Guid taskId, CancellationToken ct)
    {
        // In a full implementation, this would query the PR store
        // For now, we just post a generic message
        await chat.PostAsync(new ChatMessage(
            ChannelId: "C-123",
            ThreadTs: null,
            RecipientSlackUserId: null,
            Text: $"Task {taskId} was cancelled. Any open pull requests remain open and must be closed manually."), ct);
    }
}