using Orchestrator.Application.Approvals;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class ApprovalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(ApprovalService Service, Approval Approval)> PendingPlanApprovalAsync(
        string hash,
        string[] approvers,
        DateTimeOffset? expiresAt = null)
    {
        var store = new InMemoryApprovalStore();
        var audit = new RecordingAuditWriter();
        var service = new ApprovalService(store, audit);
        var approval = await service.RequestAsync(
            Guid.CreateVersion7(),
            "plan",
            hash,
            ActionKind.DomainWrite,
            "test-resource",
            approvers,
            expiresAt ?? Now.AddHours(24),
            default);
        return (service, approval);
    }

    [Fact]
    public async Task RecordDecision_AuthorizedApproverMatchingHash_IsAccepted()
    {
        var (service, approval) = await PendingPlanApprovalAsync(hash: "sha256:abc", approvers: ["U-owner"]);
        var result = await service.RecordDecisionAsync(approval.Id, "U-owner",
            ApprovalDecision.Approved, "sha256:abc", Now, default);
        result.Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task RecordDecision_DifferentSubjectHash_IsRejected()
    {
        var (service, approval) = await PendingPlanApprovalAsync(hash: "sha256:abc", approvers: ["U-owner"]);
        var result = await service.RecordDecisionAsync(approval.Id, "U-owner",
            ApprovalDecision.Approved, "sha256:CHANGED", Now, default);

        result.Accepted.ShouldBeFalse();
        result.Reason.ShouldContain("revision");
    }

    [Fact]
    public async Task RecordDecision_UnauthorizedApprover_IsRejected()
    {
        var (service, approval) = await PendingPlanApprovalAsync(hash: "sha256:abc", approvers: ["U-owner"]);
        var result = await service.RecordDecisionAsync(approval.Id, "U-random",
            ApprovalDecision.Approved, "sha256:abc", Now, default);

        result.Accepted.ShouldBeFalse();
        result.Reason.ShouldContain("not an eligible approver");
    }

    [Fact]
    public async Task RecordDecision_AfterExpiry_IsRejected()
    {
        var (service, approval) = await PendingPlanApprovalAsync("sha256:abc", ["U-owner"],
            expiresAt: Now.AddHours(2));
        var result = await service.RecordDecisionAsync(approval.Id, "U-owner",
            ApprovalDecision.Approved, "sha256:abc", Now.AddHours(3), default);

        result.Accepted.ShouldBeFalse();
        result.Reason.ShouldContain("expired");
    }

    [Fact]
    public async Task RecordDecision_Twice_SecondIsRejected()
    {
        var (service, approval) = await PendingPlanApprovalAsync("sha256:abc", ["U-owner"]);
        await service.RecordDecisionAsync(approval.Id, "U-owner", ApprovalDecision.Approved, "sha256:abc", Now, default);
        var second = await service.RecordDecisionAsync(approval.Id, "U-owner",
            ApprovalDecision.Rejected, "sha256:abc", Now, default);

        second.Accepted.ShouldBeFalse();
        second.Reason.ShouldContain("already decided");
    }

    [Fact]
    public async Task SilenceUntilExpiry_NeverBecomesApproval()
    {
        var (service, approval) = await PendingPlanApprovalAsync("sha256:abc", ["U-owner"],
            expiresAt: Now.AddHours(1));
        var expired = await service.ExpireDueAsync(Now.AddHours(2), default);

        expired.ShouldBe(1);
        (await service.GetAsync(approval.Id, default)).Decision.ShouldBe(ApprovalDecision.Expired);
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task RecordAsync(AuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
