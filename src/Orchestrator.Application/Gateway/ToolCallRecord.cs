using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Application.Gateway;

/// <summary>
/// Persistence for tool-call intents and outcomes, keyed by idempotency key.
/// </summary>
public interface IToolCallStore
{
    Task<ToolCallRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);

    Task<ToolCallRecord> RecordIntentAsync(ToolCall call, PolicyDecision decision, DateTimeOffset now, CancellationToken ct);

    Task RecordSuccessAsync<TResult>(Guid recordId, TResult? value, DateTimeOffset now, CancellationToken ct);

    TResult? Deserialize<TResult>(ToolCallRecord record);

    Task<bool> HasUnknownOutcomesAsync(Guid taskId, CancellationToken ct);
}

/// <summary>
/// A non-idempotent action whose outcome is unknown must never be blindly
/// retried. Fatal for the step; reconciliation decides.
/// </summary>
public sealed class UnreconciledOperationException : Exception
{
    public UnreconciledOperationException(string idempotencyKey)
        : base($"Operation '{idempotencyKey}' has an unknown outcome and must be reconciled before retry.")
    {
        IdempotencyKey = idempotencyKey;
    }

    public string IdempotencyKey { get; }
}
