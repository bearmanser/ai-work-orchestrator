using Orchestrator.Application.Gateway;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Application.Operations;

/// <summary>
/// The result of one reconciliation pass over a task's Unknown tool-call outcomes.
/// <see cref="UnresolvedTargets"/> is what lets an operator tell "checked and it was
/// fine" apart from "nobody checked" - it is empty only when every Unknown record
/// was actually resolved one way or the other.
/// </summary>
public sealed record ReconciliationSummary(
    int Resolved,
    int Failed,
    int StillUnknown,
    IReadOnlyList<ActionKind> UnresolvedTargets)
{
    public bool FullyReconciled => StillUnknown == 0;
}

/// <summary>
/// Resolves a task's <see cref="ToolCallOutcome.Unknown"/> tool-call records against
/// remote state, one registered <see cref="IOperationReconciler"/> at a time. A
/// record whose <see cref="ToolCallRecord.Action"/> has no registered reconciler -
/// or whose reconciler returns <see cref="ReconciliationOutcome.Indeterminate"/> -
/// is left exactly as it was: still <see cref="ToolCallOutcome.Unknown"/>, still
/// blocking the retry gate. This service never marks a record resolved by fiat.
/// </summary>
public sealed class OperationReconciliationService(
    IToolCallStore store, IEnumerable<IOperationReconciler> reconcilers, TimeProvider clock)
{
    public async Task<ReconciliationSummary> ReconcileAsync(Guid taskId, CancellationToken ct)
    {
        var byTarget = reconcilers
            .GroupBy(r => r.Target)
            .ToDictionary(g => g.Key, g => g.First());

        var unknown = await store.FindUnknownAsync(taskId, ct);

        var resolved = 0;
        var failed = 0;
        var unresolvedTargets = new List<ActionKind>();

        foreach (var record in unknown)
        {
            if (!byTarget.TryGetValue(record.Action, out var reconciler))
            {
                unresolvedTargets.Add(record.Action);
                continue;
            }

            var outcome = await reconciler.ReconcileAsync(record, ct);
            switch (outcome)
            {
                case ReconciliationOutcome.Succeeded:
                    await store.ResolveAsync(record.Id, ToolCallOutcome.Succeeded, clock.GetUtcNow(), ct);
                    resolved++;
                    break;
                case ReconciliationOutcome.Failed:
                    await store.ResolveAsync(record.Id, ToolCallOutcome.Failed, clock.GetUtcNow(), ct);
                    failed++;
                    break;
                default:
                    unresolvedTargets.Add(record.Action);
                    break;
            }
        }

        return new ReconciliationSummary(
            resolved, failed, unresolvedTargets.Count, unresolvedTargets.Distinct().ToList());
    }
}
