namespace Orchestrator.Application.Ports;

public interface ILeaseManager
{
    Task<bool> TryAcquireAsync(Guid stepId, string ownerId, TimeSpan duration, CancellationToken ct);
    Task<bool> HeartbeatAsync(Guid stepId, string ownerId, TimeSpan extension, CancellationToken ct);
    Task<int> ReclaimExpiredAsync(CancellationToken ct);
}
