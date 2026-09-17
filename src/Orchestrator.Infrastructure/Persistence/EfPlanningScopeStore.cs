using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Plans;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Task, envelope, and ledger reads for the planning step.</summary>
public sealed class EfPlanningScopeStore(OrchestratorDbContext db) : IPlanningScopeStore
{
    public async Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct) =>
        await db.Tasks.SingleAsync(t => t.Id == taskId, ct);

    public Task SaveTaskAsync(WorkTask task, CancellationToken ct) => db.SaveChangesAsync(ct);

    public Task<AuthorizationEnvelope?> GetEnvelopeAsync(Guid taskId, CancellationToken ct) =>
        db.AuthorizationEnvelopes.FirstOrDefaultAsync(e => e.TaskId == taskId, ct);

    public Task<BudgetLedger?> GetLedgerAsync(Guid taskId, CancellationToken ct) =>
        db.Budgets.FirstOrDefaultAsync(l => l.TaskId == taskId, ct);
}
