using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Identity;
using Orchestrator.Domain.Identity;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>
/// Durable replacement for <see cref="InMemoryPersonStore"/>. Person mappings
/// (Slack/Azure DevOps identity, ownership) are task-independent reference data
/// that must survive a process restart exactly like everything else the
/// orchestrator depends on mid-task - an in-memory singleton silently loses
/// them the moment the process recycles, which breaks resuming a task that is
/// waiting on a human answer (Task 49 acceptance suite exists to catch this).
/// </summary>
public sealed class EfPersonStore(OrchestratorDbContext db) : IPersonStore
{
    public async Task AddAsync(PersonIdentity person, CancellationToken ct)
    {
        db.People.Add(person);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<PersonIdentity?> FindBySlackUserIdAsync(string slackUserId, CancellationToken ct) =>
        db.People.AsNoTracking().FirstOrDefaultAsync(p => p.SlackUserId == slackUserId, ct);

    public Task<PersonIdentity?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<PersonIdentity>> ListAsync(CancellationToken ct) =>
        await db.People.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (person is null)
            return;
        db.People.Remove(person);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
