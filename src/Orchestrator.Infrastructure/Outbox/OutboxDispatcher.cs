using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Messaging;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Outbox;

public sealed class OutboxDispatcher(
    Func<OrchestratorDbContext> contextFactory,
    Func<OutboxMessage, CancellationToken, Task> handler,
    TimeProvider clock)
{
    public async Task<int> DispatchBatchAsync(int batchSize, CancellationToken ct)
    {
        await using var db = contextFactory();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();

        var batch = await db.Outbox
            .FromSqlInterpolated($"""
                SELECT * FROM orchestrator.outbox_messages
                WHERE processed_at IS NULL
                  AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                ORDER BY occurred_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var dispatched = 0;
        foreach (var message in batch)
        {
            try
            {
                await handler(message, ct);
                message.MarkProcessed(clock.GetUtcNow());
                dispatched++;
            }
            catch (Exception ex)
            {
                message.MarkFailed(ex.ToString(), clock.GetUtcNow());
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return dispatched;
    }
}
