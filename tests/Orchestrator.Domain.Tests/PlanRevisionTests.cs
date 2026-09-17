using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Plans;
using Shouldly;

namespace Orchestrator.Domain.Tests;

public sealed class PlanRevisionTests
{
    private static readonly Guid BackendId = Guid.CreateVersion7();
    private static readonly Guid FrontendId = Guid.CreateVersion7();

    private static readonly PlannedRepositoryChange Backend =
        new(BackendId, "backend", ChangeIntent.Change, "abc");

    private static readonly PlannedRepositoryChange Frontend =
        new(FrontendId, "frontend", ChangeIntent.Change, "def");

    private static readonly BudgetLimits Budget = new(
        MaxUsd: 25m,
        MaxWallClock: TimeSpan.FromHours(4),
        MaxAgentInvocations: 10,
        MaxCorrectionAttempts: 3);

    [Fact]
    public void ComputeHash_IdenticalContent_IsStable() =>
        PlanRevision.ComputeHash(Content()).ShouldBe(PlanRevision.ComputeHash(Content()));

    [Fact]
    public void ComputeHash_DifferentObjective_Differs() =>
        PlanRevision.ComputeHash(Content(objective: "a"))
            .ShouldNotBe(PlanRevision.ComputeHash(Content(objective: "b")));

    [Fact]
    public void ComputeHash_SameTextButDifferentBaselineCommit_Differs()
    {
        // The repository moved under the plan; the confirmation must not carry over.
        PlanRevision.ComputeHash(Content(baseline: "abc"))
            .ShouldNotBe(PlanRevision.ComputeHash(Content(baseline: "def")));
    }

    [Fact]
    public void ComputeHash_ReorderedScopeItems_Differs() =>
        // Order is meaningful in a plan a human read and agreed to.
        PlanRevision.ComputeHash(Content(scope: ["a", "b"]))
            .ShouldNotBe(PlanRevision.ComputeHash(Content(scope: ["b", "a"])));

    [Fact]
    public void ComputeHash_AddedChangeTarget_Differs() =>
        PlanRevision.ComputeHash(Content(repositories: [Backend]))
            .ShouldNotBe(PlanRevision.ComputeHash(Content(repositories: [Backend, Frontend])));

    private static PlanContent Content(
        string objective = "Add ordering endpoint",
        string baseline = "abc",
        IReadOnlyList<string>? scope = null,
        IReadOnlyList<PlannedRepositoryChange>? repositories = null) => new(
        Objective: objective,
        Scope: scope ?? ["api"],
        Exclusions: ["deployments"],
        Approach: "Add a controller plus tests",
        RepositoryChanges: repositories ?? [Backend with { BaselineCommit = baseline }],
        Tests: ["dotnet test"],
        Risks: ["none known"],
        OpenQuestions: [],
        Budget: Budget);
}
