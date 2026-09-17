using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Steps;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the agent run store.</summary>
public sealed class EfAgentRunStore(OrchestratorDbContext db) : IAgentRunStore
{
    public async Task AddAsync(AgentRun run, CancellationToken ct)
    {
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AgentRun>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
        await db.AgentRuns
            .Where(r => r.TaskId == taskId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
}
