namespace Orchestrator.Application.Ports;

public interface ICodingAgent
{
    string Name { get; }

    Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct);
}
