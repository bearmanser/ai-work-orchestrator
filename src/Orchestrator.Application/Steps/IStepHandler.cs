using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Steps;

public sealed record StepContext(Guid TaskId, Guid StepId, string Capability, string? InputArtifactId);

public sealed record StepOutcome(TaskTrigger? Trigger, string? OutputArtifactId, string? FailureReason);

public interface IStepHandler
{
    string Capability { get; }
    Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct);
}
