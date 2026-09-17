using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Steps;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the step store.</summary>
public sealed class EfStepStore(OrchestratorDbContext db) : IStepStore
{
    public async Task<IReadOnlyList<Step>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
        await db.Steps
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
}
