namespace Orchestrator.Domain.Authorization;

/// <summary>
/// A human approval request bound to the exact artifact revision that was
/// reviewed (<see cref="SubjectHash"/>), the eligible approvers snapshotted at
/// request time (<see cref="RequestedFrom"/>) and an expiry (<see cref="ExpiresAt"/>).
/// </summary>
public sealed class Approval
{
    private Approval() { } // EF

    public Approval(
        Guid taskId,
        string approvalKind,
        string subjectHash,
        ActionKind action,
        string resourceDescription,
        IReadOnlyList<string> requestedFrom,
        DateTimeOffset expiresAt,
        string policyVersion)
    {
        if (string.IsNullOrWhiteSpace(approvalKind))
            throw new ArgumentException("Approval kind is required.", nameof(approvalKind));
        if (string.IsNullOrWhiteSpace(subjectHash))
            throw new ArgumentException("Subject hash is required.", nameof(subjectHash));
        if (string.IsNullOrWhiteSpace(resourceDescription))
            throw new ArgumentException("Resource description is required.", nameof(resourceDescription));
        ArgumentNullException.ThrowIfNull(requestedFrom);
        if (string.IsNullOrWhiteSpace(policyVersion))
            throw new ArgumentException("Policy version is required.", nameof(policyVersion));

        Id = Guid.CreateVersion7();
        TaskId = taskId;
        ApprovalKind = approvalKind;
        SubjectHash = subjectHash;
        Action = action;
        ResourceDescription = resourceDescription;
        RequestedFrom = requestedFrom.ToArray();
        Decision = ApprovalDecision.Pending;
        ExpiresAt = expiresAt.ToUniversalTime();
        PolicyVersion = policyVersion;
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public string ApprovalKind { get; private set; } = null!;
    public string SubjectHash { get; private set; } = null!;
    public ActionKind Action { get; private set; }
    public string ResourceDescription { get; private set; } = null!;
    public IReadOnlyList<string> RequestedFrom { get; private set; } = [];
    public string? ApproverId { get; private set; }
    public ApprovalDecision Decision { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string PolicyVersion { get; private set; } = null!;

    public void RecordDecision(string approverId, ApprovalDecision decision, DateTimeOffset decidedAt)
    {
        ApproverId = approverId;
        Decision = decision;
        DecidedAt = decidedAt.ToUniversalTime();
    }

    public void MarkExpired()
    {
        Decision = ApprovalDecision.Expired;
    }
}
