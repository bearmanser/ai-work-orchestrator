using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;

namespace Orchestrator.Application.Gateway;

/// <summary>
/// The single choke point where a capability turns into a real external call.
/// Adapters are never resolved directly by step handlers.
/// </summary>
public sealed class ToolGateway(
    PolicyEngine policy, IToolCallStore store, IAuditWriter audit, TimeProvider clock) : IToolGateway
{
    public async Task<ToolResult<TResult>> InvokeAsync<TResult>(
        ToolCall call, Func<CancellationToken, Task<TResult>> invoke, CancellationToken ct)
    {
        var existing = await store.FindByIdempotencyKeyAsync(call.IdempotencyKey, ct).ConfigureAwait(false);
        if (existing is { Outcome: ToolCallOutcome.Succeeded })
            return new ToolResult<TResult>(true, store.Deserialize<TResult>(existing), existing.Decision);

        if (existing is { Outcome: ToolCallOutcome.Unknown })
            throw new UnreconciledOperationException(call.IdempotencyKey);

        var decision = await policy.EvaluateAsync(call.ToPolicyRequest(clock.GetUtcNow()), ct).ConfigureAwait(false);
        if (decision is not PolicyDecision.Permitted)
            return new ToolResult<TResult>(false, default, decision);

        var record = await store.RecordIntentAsync(call, decision, clock.GetUtcNow(), ct).ConfigureAwait(false);
        try
        {
            var value = await invoke(ct).ConfigureAwait(false);
            await store.RecordSuccessAsync(record.Id, value, clock.GetUtcNow(), ct).ConfigureAwait(false);
            return new ToolResult<TResult>(true, value, decision);
        }
        catch (Exception ex)
        {
            // Deliberately leave the outcome Unknown: an exception does not prove
            // the remote side effect did not occur. Reconciliation decides.
            await audit.RecordAsync(new AuditEntry("orchestrator", call.TaskId, "tool.invoke",
                ex.Message, "Unknown", call.OperationId, null), ct).ConfigureAwait(false);
            throw;
        }
    }
}
