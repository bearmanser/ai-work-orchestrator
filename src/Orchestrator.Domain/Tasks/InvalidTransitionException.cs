namespace Orchestrator.Domain.Tasks;

public sealed class InvalidTransitionException(TaskState from, TaskTrigger trigger)
    : InvalidOperationException($"Task cannot go from {from} via {trigger}")
{
    public TaskState From { get; } = from;
    public TaskTrigger Trigger { get; } = trigger;
}
