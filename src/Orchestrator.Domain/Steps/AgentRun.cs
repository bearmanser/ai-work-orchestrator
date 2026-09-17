namespace Orchestrator.Domain.Steps;

/// <summary>
/// One agent invocation against one repository, with its raw result. Durable so
/// verification (and review follow-up) can read the agent's claimed checks
/// long after the step that ran it.
/// </summary>
public sealed class AgentRun
{
    private AgentRun() { }

    public AgentRun(Guid taskId, Guid stepId, Guid repositoryId, string resultJson, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            throw new ArgumentException("Result JSON is required.", nameof(resultJson));

        Id = Guid.CreateVersion7();
        TaskId = taskId;
        StepId = stepId;
        RepositoryId = repositoryId;
        ResultJson = resultJson;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid StepId { get; private set; }
    public Guid RepositoryId { get; private set; }
    public string ResultJson { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}
