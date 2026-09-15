namespace Orchestrator.Application.Ports;

public interface IJobScheduler
{
    Task EnqueueStepAsync(Guid stepId, CancellationToken ct);
    Task RequestCancellationAsync(Guid taskId, CancellationToken ct);
}
