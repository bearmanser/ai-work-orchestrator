using Orchestrator.Domain.Steps;

namespace Orchestrator.Application.Steps;

/// <summary>Reads step rows. Implemented in Infrastructure; the steps table already exists.</summary>
public interface IStepStore
{
    Task<IReadOnlyList<Step>> ListForTaskAsync(Guid taskId, CancellationToken ct);
}
