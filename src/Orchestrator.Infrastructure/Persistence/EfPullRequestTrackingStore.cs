using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.PullRequests;
using Orchestrator.Domain.PullRequests;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the pull request tracking store.</summary>
public sealed class EfPullRequestTrackingStore(OrchestratorDbContext db) : IPullRequestTrackingStore
{
    public async Task<IReadOnlyList<PullRequestTracking>> ListByTaskAsync(Guid taskId, CancellationToken ct) =>
        await db.PullRequestTracking
            .Where(t => t.TaskId == taskId)
            .OrderBy(t => t.RepositoryId)
            .ToListAsync(ct);

    public async Task UpsertAsync(PullRequestTracking tracking, CancellationToken ct)
    {
        var existing = await db.PullRequestTracking
            .SingleOrDefaultAsync(t => t.TaskId == tracking.TaskId && t.RepositoryId == tracking.RepositoryId, ct);
        if (existing is null)
            db.PullRequestTracking.Add(tracking);

        await db.SaveChangesAsync(ct);
    }
}
