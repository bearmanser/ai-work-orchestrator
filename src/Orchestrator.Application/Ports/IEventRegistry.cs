namespace Orchestrator.Application.Ports;

public readonly record struct EventRegistration(bool IsFirstDelivery, Guid EventId);

public interface IEventRegistry
{
    Task<EventRegistration> TryRegisterAsync(
        string source, string externalEventId, string payloadHash, CancellationToken ct);
}
