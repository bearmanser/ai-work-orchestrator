namespace Orchestrator.Domain.Tasks;

public sealed class WorkTask
{
    private WorkTask() { } // EF

    public WorkTask(string objective, string requesterId, TaskSource source,
                    string correlationId, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("Objective is required", nameof(objective));
        if (string.IsNullOrWhiteSpace(requesterId))
            throw new ArgumentException("Requester is required", nameof(requesterId));
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required", nameof(correlationId));

        Id = Guid.CreateVersion7();
        Objective = objective;
        RequesterId = requesterId;
        Source = source;
        CorrelationId = correlationId;
        State = TaskState.Received;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public string Objective { get; private set; } = null!;
    public string RequesterId { get; private set; } = null!;
    public TaskSource Source { get; private set; }
    public string CorrelationId { get; private set; } = null!;
    public TaskState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public void Retitle(string objective, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("Objective is required", nameof(objective));
        Objective = objective;
        UpdatedAt = now.ToUniversalTime();
    }

    public void Fire(TaskTrigger trigger, DateTimeOffset now)
    {
        if (!TaskStateMachine.TryTransition(State, trigger, out var next))
            throw new InvalidTransitionException(State, trigger);

        TaskPhaseMetrics.RecordPhaseDuration(State, UpdatedAt, now);
        State = next;
        UpdatedAt = now.ToUniversalTime();
    }
}
