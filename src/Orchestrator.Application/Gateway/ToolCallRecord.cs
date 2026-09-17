using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Application.Gateway;

/// <summary>
/// Persistence for tool-call intents and outcomes, keyed by idempotency key.
/// </summary>
public interface IToolCallStore
{
    Task<ToolCallRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Records intent to perform <paramref name="call"/>. There is exactly one row
    /// per <see cref="ToolCallRecord.IdempotencyKey"/>, never two - two rows for the
    /// same logical operation would make "is this operation still in flight?"
    /// ambiguous, which is exactly what the retry gate depends on being answerable.
    /// So when a record already exists for this key:
    /// <list type="bullet">
    /// <item><see cref="ToolCallOutcome.Unknown"/> - still unresolved/in flight;
    /// throws <see cref="UnreconciledOperationException"/> rather than silently
    /// reusing the row, which would defeat the retry gate.</item>
    /// <item><see cref="ToolCallOutcome.Succeeded"/> - the non-idempotent action
    /// already happened; throws <see cref="OperationAlreadySucceededException"/>
    /// rather than implicitly redoing it.</item>
    /// <item><see cref="ToolCallOutcome.Failed"/> - confirmed not to have happened;
    /// the row is reused via <see cref="ToolCallRecord.ResetForRetry"/> for the new
    /// attempt, and no new row is inserted.</item>
    /// </list>
    /// </summary>
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

/// <summary>
/// A non-idempotent action that already succeeded must not be silently redone.
/// Unlike <see cref="ToolCallOutcome.Failed"/> (confirmed absent - safe to retry),
/// re-running a confirmed success is not obviously safe for any action this
/// codebase currently records intent for, so it is refused rather than guessed at.
/// </summary>
public sealed class OperationAlreadySucceededException : Exception
{
    public OperationAlreadySucceededException(string idempotencyKey)
        : base($"Operation '{idempotencyKey}' already succeeded and must not be retried.")
    {
        IdempotencyKey = idempotencyKey;
    }

    public string IdempotencyKey { get; }
}
