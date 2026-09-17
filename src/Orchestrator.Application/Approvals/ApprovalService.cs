using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;

namespace Orchestrator.Application.Approvals;

public sealed record ApprovalResult(bool Accepted, string Reason);

public interface IApprovalStore
{
    Task AddAsync(Approval approval, CancellationToken ct);

    Task<Approval?> GetAsync(Guid approvalId, CancellationToken ct);

    Task<IReadOnlyList<Approval>> ListAsync(CancellationToken ct);
}

public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly Dictionary<Guid, Approval> approvals = [];
    private readonly object gate = new();

    public Task AddAsync(Approval approval, CancellationToken ct)
    {
        lock (gate)
        {
            approvals[approval.Id] = approval;
        }

        return Task.CompletedTask;
    }

    public Task<Approval?> GetAsync(Guid approvalId, CancellationToken ct)
    {
        lock (gate)
        {
            approvals.TryGetValue(approvalId, out var approval);
            return Task.FromResult<Approval?>(approval);
        }
    }

    public Task<IReadOnlyList<Approval>> ListAsync(CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<Approval>>(approvals.Values.ToList());
        }
    }
}

/// <summary>
/// Human approvals bound to the exact reviewed revision, an eligible approver
/// and an expiry. Silence is never approval: only an explicit decision moves an
/// approval out of <see cref="ApprovalDecision.Pending"/>.
/// </summary>
public sealed class ApprovalService(IApprovalStore store, IAuditWriter auditWriter)
{
    private const string AuditAction = "approval.decided";
    private const string PolicyVersion = "v1";

    public async Task<Approval> RequestAsync(
        Guid taskId,
        string approvalKind,
        string subjectHash,
        ActionKind action,
        string resourceDescription,
        IReadOnlyList<string> requestedFrom,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var approval = new Approval(
            taskId,
            approvalKind,
            subjectHash,
            action,
            resourceDescription,
            requestedFrom,
            expiresAt,
            PolicyVersion);
        await store.AddAsync(approval, ct);
        return approval;
    }

    public async Task<Approval> GetAsync(Guid approvalId, CancellationToken ct)
    {
        var approval = await store.GetAsync(approvalId, ct);
        if (approval is null)
            throw new KeyNotFoundException($"Approval {approvalId} was not found.");
        return approval;
    }

    public Task<IReadOnlyList<Approval>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public async Task<ApprovalResult> RecordDecisionAsync(
        Guid approvalId,
        string approverId,
        ApprovalDecision decision,
        string subjectHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var approval = await GetAsync(approvalId, ct);

        if (approval.Decision is not ApprovalDecision.Pending)
            return new ApprovalResult(false, "Approval was already decided.");

        if (now > approval.ExpiresAt)
        {
            approval.MarkExpired();
            return new ApprovalResult(false, "Approval request expired.");
        }

        if (!approval.RequestedFrom.Contains(approverId))
            return new ApprovalResult(false, $"'{approverId}' is not an eligible approver for this approval.");

        if (!string.Equals(subjectHash, approval.SubjectHash, StringComparison.Ordinal))
            return new ApprovalResult(false, "The reviewed revision changed: subject hash does not match the reviewed revision.");

        approval.RecordDecision(approverId, decision, now);

        await auditWriter.RecordAsync(new AuditEntry(
            Actor: approverId,
            TaskId: approval.TaskId,
            Action: AuditAction,
            Reason: $"Approval {approval.Id} decided {decision}.",
            Outcome: decision.ToString(),
            CorrelationId: approval.Id.ToString(),
            Details: new Dictionary<string, string>
            {
                ["approvalId"] = approval.Id.ToString(),
                ["decision"] = decision.ToString(),
            }), ct);

        return new ApprovalResult(true, $"Approval {decision.ToString().ToLowerInvariant()} by {approverId}.");
    }

    public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var approvals = await store.ListAsync(ct);
        var expired = 0;
        foreach (var approval in approvals)
        {
            if (approval.Decision == ApprovalDecision.Pending && approval.ExpiresAt < now)
            {
                approval.MarkExpired();
                expired++;
            }
        }

        return expired;
    }
}
