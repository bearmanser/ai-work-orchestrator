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

    /// <summary>
    /// Puts the step into a leased state. Production lease acquisition goes through
    /// an atomic SQL compare-and-swap (<c>Orchestrator.Infrastructure.Jobs.LeaseManager</c>)
    /// to resolve the race between two workers grabbing the same step; this method
    /// exists so the aggregate's own invariants - in particular <see cref="RetryReset"/>
    /// refusing to pre-empt a live lease - can be exercised without that
    /// infrastructure.
    /// </summary>
    public void Lease(string ownerId, DateTimeOffset expiresAt)
    {
        Status = StepStatus.Leased;
        LeaseOwner = ownerId;
        LeaseExpiresAt = expiresAt;
    }

    /// <summary>
    /// Resets a non-pending step back to <see cref="StepStatus.Pending"/> for a retry,
    /// incrementing <see cref="AttemptCount"/> and clearing the failure reason and any
    /// stale lease. Callers are responsible for checking, before calling this, that no
    /// tool call for the step's task has an unreconciled <c>Unknown</c> outcome - this
    /// method only performs the step-side state reset.
    ///
    /// Refused when the step is still <see cref="StepStatus.Leased"/> with a lease
    /// that has not expired: the worker holding that lease may still be executing,
    /// and resetting it to <see cref="StepStatus.Pending"/> would let a second worker
    /// pick it up and run the same side effect twice - exactly what the lease exists
    /// to prevent. An <em>expired</em> lease is a different case: nothing is
    /// currently running, and reclaiming it is exactly what a retry should do.
    /// </summary>
    public void RetryReset(DateTimeOffset now)
    {
        if (Status == StepStatus.Pending)
            throw new InvalidOperationException($"Step {Id} is already pending; there is nothing to retry.");

        if (Status == StepStatus.Leased && LeaseExpiresAt is { } expiresAt && expiresAt > now)
        {
            throw new InvalidOperationException(
                $"Step {Id} is leased by '{LeaseOwner}' until {expiresAt:O}; refusing to reset a step " +
                "that may still be executing.");
        }

        AttemptCount++;
        Status = StepStatus.Pending;
        FailureReason = null;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }
}
