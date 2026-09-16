namespace Orchestrator.Domain.Authorization;

/// <summary>
/// The outcome of evaluating one action against an <see cref="AuthorizationEnvelope"/>,
/// a budget and a set of policy rules. This is the only vocabulary a caller acts on -
/// nothing downstream inspects <see cref="AuthorizationEnvelope.Covers"/> directly.
/// </summary>
public abstract record PolicyDecision
{
    private PolicyDecision() { }

    public sealed record Permitted(string Reason) : PolicyDecision;

    public sealed record Denied(string Reason) : PolicyDecision;

    public sealed record ApprovalRequired(string Reason, string ApprovalKind) : PolicyDecision;

    public static PolicyDecision Permit(string reason) => new Permitted(reason);

    public static PolicyDecision Deny(string reason) => new Denied(reason);

    public static PolicyDecision RequireApproval(string reason, string approvalKind) =>
        new ApprovalRequired(reason, approvalKind);
}
