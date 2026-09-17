namespace Orchestrator.Application.Operations;

/// <summary>
/// Operational kill-switches. Task 48 extends this same interface with the
/// rest of the operational commands; do not define a second one there.
/// </summary>
public interface IOperationalControls
{
    Task PauseIntakeAsync(string actor, CancellationToken ct);
    Task ResumeIntakeAsync(string actor, CancellationToken ct);
    Task<bool> IsIntakePausedAsync(CancellationToken ct);
    Task DisableIntegrationAsync(string name, string actor, CancellationToken ct);
    Task EnableIntegrationAsync(string name, string actor, CancellationToken ct);
    Task<bool> IsIntegrationDisabledAsync(string name, CancellationToken ct);
    /// <summary>
    /// Cancels the task. Returns true when the task had an open pull request at the
    /// moment of cancellation, so the caller can tell the requester it remains open -
    /// cancellation never closes a PR or reverts any other already-published effect.
    /// </summary>
    Task<bool> CancelTaskAsync(Guid taskId, string actor, CancellationToken ct);
    Task RevokeAuthorizationAsync(Guid taskId, string actor, CancellationToken ct);
    Task RetryStepAsync(Guid taskId, Guid stepId, string actor, CancellationToken ct);
    Task ReconcileTaskAsync(Guid taskId, string actor, CancellationToken ct);
}
