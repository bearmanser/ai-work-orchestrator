using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the workspace store.</summary>
public sealed class EfWorkspaceStore(OrchestratorDbContext db) : IWorkspaceStore
{
    public async Task AddAsync(Workspace workspace, CancellationToken ct)
    {
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
    }

    public Task<Workspace?> GetByIdAsync(string workspaceId, CancellationToken ct) =>
        db.Workspaces.SingleOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);

    public async Task RemoveAsync(Workspace workspace, CancellationToken ct)
    {
        db.Workspaces.Remove(workspace);
        await db.SaveChangesAsync(ct);
    }
}
