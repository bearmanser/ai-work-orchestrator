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

    /// <summary>
    /// Every record for <paramref name="taskId"/> still at
    /// <see cref="ToolCallOutcome.Unknown"/>, for reconciliation to inspect. Never
    /// guesses at an outcome itself - that is <see cref="IOperationReconciler"/>'s job.
    /// </summary>
    Task<IReadOnlyList<ToolCallRecord>> FindUnknownAsync(Guid taskId, CancellationToken ct);

    /// <summary>
    /// Persists a reconciler's resolution of one previously-<see cref="ToolCallOutcome.Unknown"/>
    /// record. <paramref name="outcome"/> must not be <see cref="ToolCallOutcome.Unknown"/> -
    /// there is nothing to persist when nothing was resolved.
    /// </summary>
    Task ResolveAsync(Guid recordId, ToolCallOutcome outcome, DateTimeOffset now, CancellationToken ct);
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
