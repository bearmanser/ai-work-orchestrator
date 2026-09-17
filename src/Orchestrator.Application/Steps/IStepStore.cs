using Orchestrator.Domain.Steps;

namespace Orchestrator.Application.Steps;

/// <summary>Reads step rows. Implemented in Infrastructure; the steps table already exists.</summary>
public interface IStepStore
{
    Task AddAsync(Step step, CancellationToken ct);
    Task<IReadOnlyList<Step>> ListForTaskAsync(Guid taskId, CancellationToken ct);
}
