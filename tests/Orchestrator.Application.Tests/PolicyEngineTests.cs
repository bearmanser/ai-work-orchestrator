using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class PolicyEngineTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid RepoA = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static AuthorizationEnvelope Envelope() => new(
        TaskId,
        "requester@kameleongruppen.no",
        [new RepositoryScope(RepoA, ["main"], ["*"], RepositoryAccess.Write)],
        ["staging"],
        ["KAMELEON-TEST"],
        [],
        [],
        [],
        [],
        [],
        [],
        new BudgetLimits(100m, TimeSpan.FromHours(1), 10, 10),
        Now.AddHours(1),
        Now.AddMinutes(-1));

    private static BudgetLedger Ledger() => new(TaskId, new BudgetLimits(100m, TimeSpan.FromHours(1), 10, 10));

    private static PolicyRequest Request(ActionKind action, ResourceScope resource) => new(
        TaskId, "requester@kameleongruppen.no", action, resource, Envelope(), Ledger(), Now);

    private static PolicyEngine Engine() => new(new RecordingAuditWriter(), PolicyRules.Default);

    [Fact]
    public async Task Evaluate_ActionInsideEnvelope_Permits()
    {
        var decision = await Engine().EvaluateAsync(Request(ActionKind.RepositoryRead,
            ResourceScope.Repository(RepoA, "main")), default);
        decision.ShouldBeOfType<PolicyDecision.Permitted>();
    }

    [Fact]
    public async Task Evaluate_ActionOutsideEnvelope_Denies()
    {
        var decision = await Engine().EvaluateAsync(Request(ActionKind.DomainRead,
            ResourceScope.P1("production", "KAMELEON-LIVE")), default);
        decision.ShouldBeOfType<PolicyDecision.Denied>();
    }

    [Theory]
    [InlineData(ActionKind.DomainWrite)]
    public async Task Evaluate_AlwaysRestrictedAction_RequiresApprovalEvenInsideEnvelope(ActionKind action)
    {
        var decision = await Engine().EvaluateAsync(Request(action,
            ResourceScope.P1("staging", "KAMELEON-TEST")), default);
        decision.ShouldBeOfType<PolicyDecision.ApprovalRequired>();
    }

    [Fact]
    public async Task Evaluate_ExhaustedBudget_DeniesBeforeCheckingScope()
    {
        var ledger = new BudgetLedger(TaskId, new BudgetLimits(1m, TimeSpan.FromHours(1), 1, 1));
        ledger.Charge(5m, TimeSpan.Zero, 0);

        var decision = await Engine().EvaluateAsync(Request(ActionKind.RepositoryRead,
            ResourceScope.Repository(RepoA, "main")) with
        { Budget = ledger }, default);

        decision.ShouldBeOfType<PolicyDecision.Denied>();
        ((PolicyDecision.Denied)decision).Reason.ShouldContain("Budget");
    }

    [Fact]
    public async Task Evaluate_RevokedEnvelope_Denies()
    {
        var envelope = Envelope();
        envelope.Revoke(Now, "operator");
        var decision = await Engine().EvaluateAsync(Request(ActionKind.RepositoryRead,
            ResourceScope.Repository(RepoA, "main")) with
        { Envelope = envelope }, default);
        decision.ShouldBeOfType<PolicyDecision.Denied>();
    }

    [Fact]
    public async Task Evaluate_EveryDecision_WritesExactlyOneAuditEntry()
    {
        var audit = new RecordingAuditWriter();
        await new PolicyEngine(audit, PolicyRules.Default).EvaluateAsync(
            Request(ActionKind.RepositoryRead, ResourceScope.Repository(RepoA, "main")), default);
        audit.Entries.Count.ShouldBe(1);
        audit.Entries[0].Action.ShouldBe("policy.evaluate");
    }

    [Fact]
    public async Task Evaluate_MergeOrDeploy_IsAlwaysDenied_NotMerelyApprovalGated()
    {
        // PROJECT_PLAN.md non-goals: automatic merges and deployments are out of scope entirely.
        var decision = await Engine().EvaluateAsync(
            Request(ActionKind.PullRequestCreate, ResourceScope.Repository(RepoA, "main"))
                with
            { Action = ActionKind.DomainWrite, Resource = ResourceScope.P1("production", "KAMELEON-LIVE") },
            default);
        decision.ShouldBeOfType<PolicyDecision.Denied>();
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
