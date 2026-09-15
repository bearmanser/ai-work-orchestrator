using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Messaging;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Inbox;

public sealed class EventRegistry(OrchestratorDbContext db, TimeProvider clock) : IEventRegistry
{
    public async Task<EventRegistration> TryRegisterAsync(
        string source, string externalEventId, string payloadHash, CancellationToken ct)
    {
        var candidate = new InboundEvent(source, externalEventId, payloadHash, clock.GetUtcNow());
        db.InboundEvents.Add(candidate);
        try
        {
            await db.SaveChangesAsync(ct);
            return new EventRegistration(true, candidate.Id);
        }
        catch (DbUpdateException ex) when ((ex.InnerException as PostgresException)?.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            db.Entry(candidate).State = EntityState.Detached;
            var existing = await db.InboundEvents.AsNoTracking()
                .SingleAsync(e => e.Source == source && e.ExternalEventId == externalEventId, ct);
            return new EventRegistration(false, existing.Id);
        }
    }
}
