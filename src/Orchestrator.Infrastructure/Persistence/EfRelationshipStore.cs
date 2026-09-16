using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the relationship store.</summary>
public sealed class EfRelationshipStore(OrchestratorDbContext db) : IRelationshipStore
{
    public async Task AddAsync(RepositoryRelationship relationship, CancellationToken ct)
    {
        db.Relationships.Add(relationship);
        await db.SaveChangesAsync(ct);
    }

    public Task<RepositoryRelationship?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Relationships.SingleOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<RepositoryRelationship>> ListForRepositoryAsync(
        Guid repositoryId, CancellationToken ct) =>
        await db.Relationships
            .Where(r => r.SourceRepositoryId == repositoryId || r.TargetRepositoryId == repositoryId)
            .OrderBy(r => r.TargetRepositoryId)
            .ToListAsync(ct);

    public async Task RemoveAsync(RepositoryRelationship relationship, CancellationToken ct)
    {
        db.Relationships.Remove(relationship);
        await db.SaveChangesAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
