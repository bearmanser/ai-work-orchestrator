using System.Text.Json;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Messaging;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Outbox;

public sealed class OutboxWriter(OrchestratorDbContext db, TimeProvider clock) : IOutboxWriter
{
    public void Enqueue(string messageType, object payload, Guid? taskId, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        db.Outbox.Add(new OutboxMessage(
            messageType,
            JsonSerializer.Serialize(payload, OutboxJson.Options),
            taskId,
            idempotencyKey,
            clock.GetUtcNow()));
    }
}

public static class OutboxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
