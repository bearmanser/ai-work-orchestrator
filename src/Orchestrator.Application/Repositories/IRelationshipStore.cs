using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Repositories;

/// <summary>Durable storage for relationships. Implemented in Infrastructure.</summary>
public interface IRelationshipStore
{
    Task AddAsync(RepositoryRelationship relationship, CancellationToken ct);
    Task<RepositoryRelationship?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RepositoryRelationship>> ListForRepositoryAsync(Guid repositoryId, CancellationToken ct);
    Task RemoveAsync(RepositoryRelationship relationship, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}
