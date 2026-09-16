using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;

namespace Orchestrator.Domain.Operations;

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