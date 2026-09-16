namespace Orchestrator.Domain.Authorization;

/// <summary>
/// The decision state of an <see cref="Approval"/>. Silence is never approval:
/// there is no path from <see cref="Pending"/> to <see cref="Approved"/> without
/// an explicit eligible-approver decision bound to the exact reviewed revision.
/// </summary>
public enum ApprovalDecision
{
    Pending,
    Approved,
    Rejected,
    Expired,
}
