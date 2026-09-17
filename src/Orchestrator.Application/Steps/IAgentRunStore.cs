using Orchestrator.Domain.Steps;

namespace Orchestrator.Application.Steps;

/// <summary>Durable storage for agent runs. Implemented in Infrastructure.</summary>
public interface IAgentRunStore
{
    Task AddAsync(AgentRun run, CancellationToken ct);
    Task<IReadOnlyList<AgentRun>> ListForTaskAsync(Guid taskId, CancellationToken ct);
}
