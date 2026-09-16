using Orchestrator.Application.Policy;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;

namespace Orchestrator.Application.Gateway;

/// <summary>
/// One capability turning into a real external call. The single choke point
/// where policy, idempotency and intent-before-effect are enforced.
/// </summary>
public sealed record ToolCall(
    Guid TaskId,
    Guid StepId,
    string Actor,
    ActionKind Action,
    ResourceScope Resource,
    string OperationId,
    string IdempotencyKey,
    string Description,
    AuthorizationEnvelope Envelope,
    BudgetLedger Budget)
{
    /// <summary>
    /// Builds the policy request for this call. The correlation id is always the
    /// call's <see cref="OperationId"/> — the engine must never mint its own.
    /// </summary>
    public PolicyRequest ToPolicyRequest(DateTimeOffset now) => new(
        TaskId,
        Actor,
        Action,
        Resource,
        Envelope,
        Budget,
        now,
        OperationId);
}

/// <summary>
/// The outcome of one gateway invocation. <see cref="Executed"/> is false when
/// policy denied or required approval and the adapter was never invoked.
/// </summary>
public sealed record ToolResult<TResult>(bool Executed, TResult? Value, PolicyDecision Decision);

public interface IToolGateway
{
    Task<ToolResult<TResult>> InvokeAsync<TResult>(
        ToolCall call,
        Func<CancellationToken, Task<TResult>> invoke,
        CancellationToken ct);
}
