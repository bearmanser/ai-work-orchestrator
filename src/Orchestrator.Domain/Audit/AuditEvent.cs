namespace Orchestrator.Domain.Audit;

/// <summary>
/// One immutable record of a policy decision, adapter call, admin command, or state
/// transition. Every such action in the system writes exactly one of these.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent() { }

    public AuditEvent(string actor, Guid? taskId, string action, string reason, string outcome,
                       string correlationId, string detailsJson, DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7();
        Actor = actor;
        TaskId = taskId;
        Action = action;
        Reason = reason;
        Outcome = outcome;
        CorrelationId = correlationId;
        DetailsJson = detailsJson;
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public string Actor { get; private set; } = null!;
    public Guid? TaskId { get; private set; }
    public string Action { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public string Outcome { get; private set; } = null!;
    public string CorrelationId { get; private set; } = null!;
    public string DetailsJson { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
}
