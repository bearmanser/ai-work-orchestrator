namespace Orchestrator.Application.Ports;

public sealed record AuditEntry(
    string Actor,
    Guid? TaskId,
    string Action,
    string Reason,
    string Outcome,
    string CorrelationId,
    IReadOnlyDictionary<string, string>? Details);

public interface IAuditWriter
{
    /// <summary>
    /// Records one audit entry. Throws <see cref="ArgumentException"/> without writing
    /// anything if any detail value looks like a secret (API key, token, etc.) - never
    /// persist secret values, only references to them.
    /// </summary>
    Task RecordAsync(AuditEntry entry, CancellationToken ct);
}
