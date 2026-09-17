using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Context;
using Orchestrator.Domain.Context;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the context snapshot store.</summary>
public sealed class EfContextSnapshotStore(OrchestratorDbContext db) : IContextSnapshotStore
{
    public async Task AddAsync(ContextSnapshot snapshot, CancellationToken ct)
    {
        db.ContextSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
    }

    public Task<ContextSnapshot?> GetAsync(Guid snapshotId, CancellationToken ct) =>
        db.ContextSnapshots.SingleOrDefaultAsync(s => s.Id == snapshotId, ct);
}
