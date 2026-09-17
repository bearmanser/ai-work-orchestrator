using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Identity;
using Orchestrator.Domain.Identity;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Durable replacement for <see cref="InMemoryOwnershipStore"/>; see <see cref="EfPersonStore"/>.</summary>
public sealed class EfOwnershipStore(OrchestratorDbContext db) : IOwnershipStore
{
    public async Task AddAsync(OwnershipEntry entry, CancellationToken ct)
    {
        db.OwnershipEntries.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OwnershipEntry>> ListAsync(CancellationToken ct) =>
        await db.OwnershipEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

    public Task<OwnershipEntry?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.OwnershipEntries.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        var entry = await db.OwnershipEntries.FirstOrDefaultAsync(o => o.Id == id, ct).ConfigureAwait(false);
        if (entry is null)
            return;
        db.OwnershipEntries.Remove(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
