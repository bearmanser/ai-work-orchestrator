using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Approvals;
using Orchestrator.Domain.Authorization;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Durable replacement for <see cref="InMemoryApprovalStore"/>; see <see cref="EfPersonStore"/>.</summary>
public sealed class EfApprovalStore(OrchestratorDbContext db) : IApprovalStore
{
    public async Task AddAsync(Approval approval, CancellationToken ct)
    {
        db.Approvals.Add(approval);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<Approval?> GetAsync(Guid approvalId, CancellationToken ct) =>
        db.Approvals.FirstOrDefaultAsync(a => a.Id == approvalId, ct);

    public async Task<IReadOnlyList<Approval>> ListAsync(CancellationToken ct) =>
        await db.Approvals.ToListAsync(ct).ConfigureAwait(false);
}
