using Orchestrator.Domain.Authorization;

namespace Orchestrator.Application.Gateway;

/// <summary>
/// The persisted state of one idempotent tool call. Intent is recorded before
/// invoking; a crash between intent and outcome leaves <see cref="Unknown"/>.
/// </summary>
public enum ToolCallOutcome
{
    Unknown,
    Succeeded,
}

/// <summary>
/// One persisted tool-call intent plus its outcome, if known.
/// </summary>
public sealed record ToolCallRecord(
    Guid Id,
    Guid TaskId,
    Guid StepId,
    string OperationId,
    string IdempotencyKey,
    ToolCallOutcome Outcome,
    PolicyDecision Decision,
    string? ResultJson,
    DateTimeOffset RecordedAt,
    DateTimeOffset? CompletedAt);

/// <summary>
/// Persistence for tool-call intents and outcomes, keyed by idempotency key.
/// </summary>
public interface IToolCallStore
{
    Task<ToolCallRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);

    Task<ToolCallRecord> RecordIntentAsync(ToolCall call, PolicyDecision decision, DateTimeOffset now, CancellationToken ct);

    Task RecordSuccessAsync<TResult>(Guid recordId, TResult? value, DateTimeOffset now, CancellationToken ct);

    TResult? Deserialize<TResult>(ToolCallRecord record);
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
