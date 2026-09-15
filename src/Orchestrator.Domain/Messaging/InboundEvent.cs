namespace Orchestrator.Domain.Messaging;

public sealed class InboundEvent
{
    private InboundEvent() { }

    public InboundEvent(string source, string externalEventId, string payloadHash, DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);

        Id = Guid.CreateVersion7();
        Source = source;
        ExternalEventId = externalEventId;
        PayloadHash = payloadHash;
        ReceivedAt = receivedAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public string Source { get; private set; } = null!;
    public string ExternalEventId { get; private set; } = null!;
    public string PayloadHash { get; private set; } = null!;
    public DateTimeOffset ReceivedAt { get; private set; }
}
