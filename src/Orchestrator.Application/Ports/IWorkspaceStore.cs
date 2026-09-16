using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Ports;

/// <summary>Durable storage for workspace rows. Implemented in Infrastructure.</summary>
public interface IWorkspaceStore
{
    Task AddAsync(Workspace workspace, CancellationToken ct);
    Task<Workspace?> GetByIdAsync(string workspaceId, CancellationToken ct);
    Task RemoveAsync(Workspace workspace, CancellationToken ct);
}
