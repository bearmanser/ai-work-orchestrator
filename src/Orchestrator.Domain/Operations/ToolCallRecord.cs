using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;

namespace Orchestrator.Domain.Operations;

/// <summary>
/// One persisted tool-call intent plus its outcome, if known. <see cref="Outcome"/>,
/// <see cref="ResultJson"/> and <see cref="CompletedAt"/> are set only once, together,
/// through <see cref="Complete"/> - recording a result is an explicit domain
/// transition, not an incidental property assignment. A crash between recording
/// intent and calling <see cref="Complete"/> leaves <see cref="ToolCallOutcome.Unknown"/>
/// in place, which is exactly the state reconciliation exists to resolve.
/// </summary>
public sealed record ToolCallRecord
{
    public ToolCallRecord(
        Guid id,
        Guid taskId,
        Guid stepId,
        ActionKind action,
        string operationId,
        string idempotencyKey,
        ToolCallOutcome outcome,
        PolicyDecision decision,
        string? resultJson,
        DateTimeOffset recordedAt,
        DateTimeOffset? completedAt)
    {
        Id = id;
        TaskId = taskId;
        StepId = stepId;
        Action = action;
        OperationId = operationId;
        IdempotencyKey = idempotencyKey;
        Outcome = outcome;
        Decision = decision;
        ResultJson = resultJson;
        RecordedAt = recordedAt;
        CompletedAt = completedAt;
    }

    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public Guid StepId { get; init; }

    /// <summary>
    /// The category of remote operation this call was - the "target" a
    /// reconciler is registered against to check whether it actually happened.
    /// </summary>
    public ActionKind Action { get; init; }

    public string OperationId { get; init; }
    public string IdempotencyKey { get; init; }
    public ToolCallOutcome Outcome { get; private set; }
    public PolicyDecision Decision { get; init; }
    public string? ResultJson { get; private set; }
    public DateTimeOffset RecordedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// Records the outcome of a previously-recorded intent. Used both by a successful
    /// invocation (outcome <see cref="ToolCallOutcome.Succeeded"/>) and by
    /// reconciliation resolving a stale <see cref="ToolCallOutcome.Unknown"/> outcome
    /// against remote state.
    /// </summary>
    public void Complete(ToolCallOutcome outcome, string? resultJson, DateTimeOffset completedAt)
    {
        Outcome = outcome;
        ResultJson = resultJson;
        CompletedAt = completedAt;
    }
}
