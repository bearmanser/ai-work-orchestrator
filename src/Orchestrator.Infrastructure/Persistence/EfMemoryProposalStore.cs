using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Memory;
using Orchestrator.Domain.Memory;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Entity Framework implementation of the memory proposal store.</summary>
public sealed class EfMemoryProposalStore(OrchestratorDbContext db) : IMemoryProposalStore
{
    public async Task AddAsync(MemoryProposal proposal, CancellationToken ct)
    {
        db.MemoryProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
    }

    public Task<MemoryProposal?> GetAsync(Guid id, CancellationToken ct) =>
        db.MemoryProposals.SingleOrDefaultAsync(p => p.Id == id, ct);
}
