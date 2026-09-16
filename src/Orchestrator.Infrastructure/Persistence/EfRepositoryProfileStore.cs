using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the repository catalog store.</summary>
public sealed class EfRepositoryProfileStore(OrchestratorDbContext db) : IRepositoryProfileStore
{
    public async Task CreateAsync(RepositoryProfile profile, CancellationToken ct)
    {
        db.Repositories.Add(profile);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException(
                $"A repository with remote '{profile.RemoteUrl}' already exists.", ex);
        }
    }

    public Task<RepositoryProfile?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Repositories.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken ct) =>
        await db.Repositories.OrderBy(r => r.Name).ToListAsync(ct);

    public Task<bool> RemoteExistsAsync(string normalizedRemoteUrl, CancellationToken ct) =>
        db.Repositories.AnyAsync(r => r.RemoteUrl == normalizedRemoteUrl, ct);

    public async Task UpdateAsync(RepositoryProfile profile, uint expectedVersion, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrentModificationException(profile.Id, expectedVersion, ex);
        }
    }
}
