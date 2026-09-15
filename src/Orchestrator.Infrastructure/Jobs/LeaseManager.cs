using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Jobs;

public sealed class LeaseManager(OrchestratorDbContext db, TimeProvider clock) : ILeaseManager
{
    public async Task<bool> TryAcquireAsync(Guid stepId, string ownerId, TimeSpan duration, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expires = now + duration;
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE orchestrator.steps
            SET lease_owner = {ownerId}, lease_expires_at = {expires}, status = 'Leased'
            WHERE id = {stepId}
              AND status IN ('Pending', 'Leased')
              AND (lease_owner IS NULL OR lease_expires_at < {now} OR lease_owner = {ownerId})
            """, ct);
        return rows == 1;
    }

    public async Task<bool> HeartbeatAsync(Guid stepId, string ownerId, TimeSpan extension, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE orchestrator.steps
            SET lease_expires_at = {now + extension}
            WHERE id = {stepId} AND lease_owner = {ownerId} AND lease_expires_at >= {now}
            """, ct);
        return rows == 1;
    }

    public async Task<int> ReclaimExpiredAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE orchestrator.steps
            SET lease_owner = NULL, lease_expires_at = NULL, status = 'Pending'
            WHERE status = 'Leased' AND lease_expires_at < {now}
            """, ct);
    }
}
