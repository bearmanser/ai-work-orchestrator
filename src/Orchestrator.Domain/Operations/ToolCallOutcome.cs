namespace Orchestrator.Domain.Operations;

/// <summary>
/// The persisted state of one idempotent tool call. Intent is recorded before
/// invoking; a crash between intent and outcome leaves <see cref="Unknown"/>.
/// </summary>
public enum ToolCallOutcome
{
    Unknown,
    Succeeded,

    /// <summary>
    /// Reconciliation checked remote state and confirmed the action did not
    /// happen - distinct from <see cref="Unknown"/> (nobody checked) and from
    /// <see cref="Succeeded"/> (it happened). A retry is safe once a record
    /// reaches this state, because the remote side effect is confirmed absent.
    /// </summary>
    Failed,
}
