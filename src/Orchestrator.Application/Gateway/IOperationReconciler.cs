using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Application.Gateway;

/// <summary>
/// The answer a reconciler gives for one <see cref="ToolCallOutcome.Unknown"/>
/// record after inspecting remote state. There is deliberately no way to say
/// "assume it worked" - a reconciler that cannot tell must say so.
/// </summary>
public enum ReconciliationOutcome
{
    /// <summary>The remote side effect happened.</summary>
    Succeeded,

    /// <summary>Checked, and it confirmably did not happen; safe to retry.</summary>
    Failed,

    /// <summary>
    /// Checked and still cannot tell (e.g. the remote system itself is unreachable).
    /// The record stays <see cref="ToolCallOutcome.Unknown"/> and the retry gate
    /// stays closed.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// Answers "did this remote side effect actually happen?" for one category of
/// tool call (<see cref="Target"/>). Reconciliation must never guess: a category
/// with no registered reconciler - or a reconciler that returns
/// <see cref="ReconciliationOutcome.Indeterminate"/> - leaves the record
/// <see cref="ToolCallOutcome.Unknown"/> and the retry gate closed. This is the
/// seam later adapters (Azure DevOps PR lookups, Slack message lookups, etc.)
/// implement against; none exist yet.
/// </summary>
public interface IOperationReconciler
{
    /// <summary>The action kind this reconciler can check remote state for.</summary>
    ActionKind Target { get; }

    Task<ReconciliationOutcome> ReconcileAsync(ToolCallRecord record, CancellationToken ct);
}
