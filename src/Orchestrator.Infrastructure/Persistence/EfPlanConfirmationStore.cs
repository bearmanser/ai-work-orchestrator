using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Plans;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the plan confirmation store.</summary>
public sealed class EfPlanConfirmationStore(OrchestratorDbContext db) : IPlanConfirmationStore
{
    public async Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct) =>
        await db.Tasks.SingleAsync(t => t.Id == taskId, ct);

    public Task SaveTaskAsync(WorkTask task, CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task AddRevisionAsync(PlanRevision revision, CancellationToken ct)
    {
        db.PlanRevisions.Add(revision);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PlanRevision> GetRevisionAsync(Guid revisionId, CancellationToken ct) =>
        await db.PlanRevisions.SingleAsync(r => r.Id == revisionId, ct);

    public Task<PlanRevision?> GetLatestPendingRevisionAsync(Guid taskId, CancellationToken ct) =>
        db.PlanRevisions
            .Where(r => r.TaskId == taskId && r.ConfirmationStatus == ConfirmationStatus.Pending)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PlanRevision>> ListRevisionsAsync(Guid taskId, CancellationToken ct) =>
        await db.PlanRevisions
            .Where(r => r.TaskId == taskId)
            .OrderBy(r => r.RevisionNumber)
            .ToListAsync(ct);

    public Task<PlanRevision?> FindByHashAsync(string contentHash, CancellationToken ct) =>
        db.PlanRevisions.SingleOrDefaultAsync(r => r.ContentHash == contentHash, ct);
}
