using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Operations;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Operations;

/// <summary>Operational kill-switches backed by the `operational_flags` table.</summary>
public sealed class EfOperationalControls(
    OrchestratorDbContext db, IAuditWriter audit, TimeProvider clock) : IOperationalControls
{
    private const string IntakePausedKey = "intake.paused";

    public async Task PauseIntakeAsync(string actor, CancellationToken ct) =>
        await SetAsync(IntakePausedKey, true, actor, "operations.pause-intake",
            "Task intake paused.", ct);

    public async Task ResumeIntakeAsync(string actor, CancellationToken ct) =>
        await SetAsync(IntakePausedKey, false, actor, "operations.resume-intake",
            "Task intake resumed.", ct);

    public async Task<bool> IsIntakePausedAsync(CancellationToken ct) =>
        await db.OperationalFlags
            .Where(f => f.Key == IntakePausedKey)
            .Select(f => f.Value)
            .SingleOrDefaultAsync(ct);

    private async Task SetAsync(
        string key, bool value, string actor, string action, string reason, CancellationToken ct)
    {
        var flag = await db.OperationalFlags.SingleOrDefaultAsync(f => f.Key == key, ct);
        if (flag is null)
        {
            flag = new OperationalFlag(key, value, clock.GetUtcNow());
            db.OperationalFlags.Add(flag);
        }
        else
        {
            flag.Set(value, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: null,
            Action: action,
            Reason: reason,
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string> { ["key"] = key }), ct);
    }
}
