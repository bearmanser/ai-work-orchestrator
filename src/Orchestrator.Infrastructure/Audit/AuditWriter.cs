using System.Text.Json;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Audit;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Audit;

public sealed class AuditWriter(OrchestratorDbContext db, TimeProvider clock) : IAuditWriter
{
    private const string RedactedPlaceholder = "[redacted]";

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        // Details are caller-constructed structured data: a secret-shaped value there is
        // a programming error the caller should fix, so we throw and write nothing.
        foreach (var (key, value) in entry.Details ?? new Dictionary<string, string>())
        {
            if (SecretShapeDetector.IsSecretShape(value))
                throw new ArgumentException($"Audit detail '{key}' looks like a secret value", nameof(entry));
        }

        // Reason is free text that is not always fully caller-controlled - a later
        // caller may feed an exception message or an HTTP failure body straight into it,
        // and either can incidentally quote a token or URL-embedded credential. Refusing
        // the write here would lose the audit record entirely, which is worse than
        // storing a sanitised one - so we redact matched spans instead of throwing.
        // PROJECT_PLAN.md section 6 requires secrets be redacted from logs and
        // artifacts, not rejected outright; Actor, Action and CorrelationId are
        // structured identifiers rather than free text and are deliberately left
        // unchecked.
        var reason = SecretShapeDetector.Redact(entry.Reason, RedactedPlaceholder);

        db.AuditEvents.Add(new AuditEvent(
            entry.Actor, entry.TaskId, entry.Action, reason, entry.Outcome,
            entry.CorrelationId, JsonSerializer.Serialize(entry.Details ?? new Dictionary<string, string>()),
            clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }
}
