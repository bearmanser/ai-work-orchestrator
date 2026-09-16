using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Audit;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Audit;

public sealed partial class AuditWriter(OrchestratorDbContext db, TimeProvider clock) : IAuditWriter
{
    // Matches the shape of common credential/token formats (GitHub PATs, Slack tokens,
    // OpenAI-style API keys, JWTs) so a caller can never persist a secret value into the
    // audit log - only a reference to one. Deliberately does not match ordinary audit
    // values such as repository names, commit SHAs, or branch names.
    [GeneratedRegex(@"(gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9]{20,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.)")]
    private static partial Regex SecretShape();

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        foreach (var (key, value) in entry.Details ?? new Dictionary<string, string>())
        {
            if (SecretShape().IsMatch(value))
                throw new ArgumentException($"Audit detail '{key}' looks like a secret value", nameof(entry));
        }

        db.AuditEvents.Add(new AuditEvent(
            entry.Actor, entry.TaskId, entry.Action, entry.Reason, entry.Outcome,
            entry.CorrelationId, JsonSerializer.Serialize(entry.Details ?? new Dictionary<string, string>()),
            clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }
}
