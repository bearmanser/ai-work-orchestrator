namespace Orchestrator.Domain.Steps;

public enum StepStatus { Pending, Leased, Succeeded, Failed, Cancelled }

public sealed class Step
{
    private Step() { }

    public Step(Guid taskId, string capability, Guid? dependsOnStepId, string? inputArtifactId,
                DateTimeOffset createdAt)
    {
        Id = Guid.CreateVersion7();
        TaskId = taskId;
        Capability = capability;
        DependsOnStepId = dependsOnStepId;
        InputArtifactId = inputArtifactId;
        Status = StepStatus.Pending;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public string Capability { get; private set; } = null!;
    public StepStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public Guid? DependsOnStepId { get; private set; }
    public string? InputArtifactId { get; private set; }
    public string? OutputArtifactId { get; private set; }
    public string? LeaseOwner { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? FailureReason { get; private set; }

    public void Succeed(string? outputArtifactId, DateTimeOffset now)
    {
        Status = StepStatus.Succeeded;
        OutputArtifactId = outputArtifactId;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        AttemptCount++;
        Status = StepStatus.Failed;
        FailureReason = reason;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    public void Cancel()
    {
        Status = StepStatus.Cancelled;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }
}
