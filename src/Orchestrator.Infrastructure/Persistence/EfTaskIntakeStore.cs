using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Slack;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>Writes the whole intake graph in one transaction.</summary>
public sealed class EfTaskIntakeStore(OrchestratorDbContext db) : ITaskIntakeStore
{
    public Task<bool> ExistsForCorrelationAsync(string correlationId, CancellationToken ct) =>
        db.Tasks.AnyAsync(t => t.CorrelationId == correlationId, ct);

    public async Task<Guid?> TryCreateAsync(TaskIntakeGraph graph, CancellationToken ct)
    {
        db.Tasks.Add(graph.Task);
        db.AuthorizationEnvelopes.Add(graph.Envelope);
        db.Budgets.Add(graph.Ledger);
        db.Steps.Add(graph.PlanningStep);
        db.Outbox.Add(graph.Message);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return null;
        }

        return graph.Task.Id;
    }
}
