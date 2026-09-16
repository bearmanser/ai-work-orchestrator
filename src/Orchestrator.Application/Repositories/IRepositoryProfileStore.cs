using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Repositories;

/// <summary>
/// Persistence for the repository catalog. The Entity Framework implementation
/// lives in Infrastructure; the service only sees this port.
/// </summary>
public interface IRepositoryProfileStore
{
    Task CreateAsync(RepositoryProfile profile, CancellationToken ct);
    Task<RepositoryProfile?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken ct);
    Task<bool> RemoteExistsAsync(string normalizedRemoteUrl, CancellationToken ct);
    Task UpdateAsync(RepositoryProfile profile, uint expectedVersion, CancellationToken ct);
}
