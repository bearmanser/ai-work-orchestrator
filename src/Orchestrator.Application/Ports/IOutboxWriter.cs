namespace Orchestrator.Application.Ports;

public interface IOutboxWriter
{
    /// <summary>Stages a message in the caller's transaction. Commit happens with SaveChangesAsync.</summary>
    void Enqueue(string messageType, object payload, Guid? taskId, string idempotencyKey);
}
