namespace Orchestrator.Domain.Messaging;

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public OutboxMessage(string messageType, string payloadJson, Guid? taskId,
                         string idempotencyKey, DateTimeOffset occurredAt)
    {
        Id = Guid.CreateVersion7();
        MessageType = messageType;
        PayloadJson = payloadJson;
        TaskId = taskId;
        IdempotencyKey = idempotencyKey;
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public string MessageType { get; private set; } = null!;
    public string PayloadJson { get; private set; } = null!;
    public Guid? TaskId { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastError { get; private set; }

    public void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt = now.ToUniversalTime();
        LastError = null;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        AttemptCount++;
        LastError = error.Length > 4000 ? error[..4000] : error;
        // Exponential backoff capped at 10 minutes.
        var delay = TimeSpan.FromSeconds(Math.Min(600, Math.Pow(2, Math.Min(AttemptCount, 10))));
        NextAttemptAt = now.ToUniversalTime() + delay;
    }
}
