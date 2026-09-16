using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;

namespace Orchestrator.Application.Policy;

/// <summary>
/// One request to perform <see cref="Action"/> against <see cref="Resource"/>, to be
/// checked against <see cref="Envelope"/> and <see cref="Budget"/> as of <see cref="Now"/>.
/// </summary>
public sealed record PolicyRequest(
    Guid TaskId,
    string Actor,
    ActionKind Action,
    ResourceScope Resource,
    AuthorizationEnvelope Envelope,
    BudgetLedger Budget,
    DateTimeOffset Now);

/// <summary>
/// The single place an action, actor, resource and envelope become an allow/deny/
/// require-approval decision. Authorization is enforced here, outside the model: an
/// agent may propose an action, but only this engine - and, downstream, the
/// ToolGateway acting on a <see cref="PolicyDecision.Permitted"/> - decides whether it
/// executes.
///
/// <see cref="AuthorizationEnvelope.Covers"/> is one necessary condition among several,
/// never the decision by itself. In particular, <see cref="ActionKind.DomainWrite"/>,
/// <see cref="ActionKind.MemoryWrite"/>, <see cref="ActionKind.ContactPerson"/> and
/// <see cref="ActionKind.PostMessage"/> are checked by the envelope against the same
/// flat allow-list as their read counterparts (there is no separate write-scope field
/// for those four resource kinds), so this engine layers <see cref="PolicyRules.AlwaysRequiresApproval"/>
/// and the production-environment denial on top rather than trusting Covers alone.
///
/// The evaluation order is fixed and load-bearing - a cheaper, harder rule wins so the
/// reason reported to the caller is the one that actually applies:
/// 1. Envelope revoked or expired -> Denied.
/// 2. Budget exhausted -> Denied.
/// 3. Resource targets a forbidden (production) P1 environment -> Denied, unconditionally.
/// 4. Envelope does not cover the action -> Denied.
/// 5. Action is always-approval-gated -> ApprovalRequired.
/// 6. Otherwise -> Permitted.
///
/// Every call writes exactly one audit entry, whatever the outcome.
/// </summary>
public sealed class PolicyEngine(IAuditWriter auditWriter, PolicyRules rules)
{
    private const string AuditAction = "policy.evaluate";

    public async Task<PolicyDecision> EvaluateAsync(PolicyRequest request, CancellationToken ct)
    {
        var decision = Evaluate(request);

        await auditWriter.RecordAsync(new AuditEntry(
            Actor: request.Actor,
            TaskId: request.TaskId,
            Action: AuditAction,
            Reason: ReasonOf(decision),
            Outcome: decision.GetType().Name,
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string> { ["action"] = request.Action.ToString() }), ct);

        return decision;
    }

    private PolicyDecision Evaluate(PolicyRequest request)
    {
        var envelope = request.Envelope;

        // 1. Envelope revoked or expired.
        if (envelope.RevokedAt is not null && request.Now >= envelope.RevokedAt.Value)
        {
            return PolicyDecision.Deny(envelope.RevocationReason is null
                ? "Authorization envelope was revoked."
                : $"Authorization envelope was revoked: {envelope.RevocationReason}.");
        }

        if (request.Now >= envelope.ExpiresAt)
            return PolicyDecision.Deny("Authorization envelope has expired.");

        // 2. Budget exhausted.
        try
        {
            request.Budget.EnsureWithinLimits();
        }
        catch (BudgetExceededException ex)
        {
            return PolicyDecision.Deny($"Budget exhausted: {ex.Dimension}.");
        }

        // 3. Production P1 environments are an unconditional non-goal, not an
        // approval prompt - this must be checked and denied before we even ask
        // whether the envelope covers the action.
        if (request.Resource is ResourceScope.P1Resource p1 && rules.ForbiddenEnvironments.Contains(p1.Environment))
        {
            return PolicyDecision.Deny(
                $"Environment '{p1.Environment}' is a forbidden production environment; " +
                "automatic writes to production are out of scope and can never be approved.");
        }

        // 4. The envelope itself must cover the action against the resource.
        if (!envelope.Covers(request.Action, request.Resource, request.Now))
        {
            return PolicyDecision.Deny(
                $"Authorization envelope does not cover {request.Action} for the requested resource.");
        }

        // 5. Some actions always require a human even when the envelope covers
        // them - see the type remarks for why this compensates for the envelope's
        // lack of a write axis on four action kinds.
        if (rules.AlwaysRequiresApproval.Contains(request.Action))
        {
            return PolicyDecision.RequireApproval(
                $"{request.Action} always requires human approval, regardless of envelope scope.",
                request.Action.ToString());
        }

        // 6. Otherwise, permitted.
        return PolicyDecision.Permit(
            "Action is within the authorization envelope and does not require approval.");
    }

    private static string ReasonOf(PolicyDecision decision) => decision switch
    {
        PolicyDecision.Permitted permitted => permitted.Reason,
        PolicyDecision.Denied denied => denied.Reason,
        PolicyDecision.ApprovalRequired approvalRequired => approvalRequired.Reason,
        _ => throw new InvalidOperationException($"Unknown policy decision type: {decision.GetType()}."),
    };
}
