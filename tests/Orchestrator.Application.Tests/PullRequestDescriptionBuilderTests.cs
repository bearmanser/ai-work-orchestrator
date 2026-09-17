using Orchestrator.Application.Evidence;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Plans;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class PullRequestDescriptionBuilderTests
{
    private static readonly PlanRevision Plan = RevisionWithHash("sha256:abc123");

    [Fact]
    public void Build_SeparatesLocalChecksFromDomainChecks()
    {
        var description = PullRequestDescriptionBuilder.Build(
            EvidenceWith(runtimeChecks: [Check("dotnet test", true)],
                         domainChecks: [DomainCheck("staging", "KAMELEON-TEST", "order count", true)]),
            Plan, siblings: []);

        var localSection = Section(description, "Local checks");
        var domainSection = Section(description, "P1 checks");

        localSection.ShouldContain("dotnet test");
        localSection.ShouldNotContain("KAMELEON-TEST");
        domainSection.ShouldContain("staging");
    }

    [Fact]
    public void Build_WithNoDomainChecks_SaysSoExplicitly()
    {
        var description = PullRequestDescriptionBuilder.Build(EvidenceWith(domainChecks: []), Plan, []);

        description.ShouldContain("No P1 validation was performed");
    }

    [Fact]
    public void Build_NeverDescribesALocalTestAsStagingVerification()
    {
        var description = PullRequestDescriptionBuilder.Build(
            EvidenceWith(runtimeChecks: [Check("dotnet test", true)], domainChecks: []), Plan, []);

        description.ShouldNotContain("verified in staging");
        description.ShouldContain("No P1 validation was performed");
    }

    [Fact]
    public void Build_LinksTheConfirmedPlanRevision()
    {
        var description = PullRequestDescriptionBuilder.Build(EvidenceWith(), Plan, []);

        description.ShouldContain(Plan.ContentHash);
        Plan.ContentHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public void Build_WithSiblingPullRequests_LinksThemAsRequired()
    {
        var description = PullRequestDescriptionBuilder.Build(EvidenceWith(), Plan,
            [new PullRequestRef(4712, "https://dev.azure.com/kameleon/_git/frontend/pullrequest/4712",
                                "orchestrator/task-1", "main", "abc")]);

        description.ShouldContain("4712");
        description.ShouldContain("must also be approved");
    }

    [Fact]
    public void Build_WithFailedCheck_MarksItFailedRatherThanOmittingIt()
    {
        var description = PullRequestDescriptionBuilder.Build(
            EvidenceWith(runtimeChecks: [Check("dotnet test", false)]), Plan, []);

        description.ShouldContain("dotnet test");
        description.ShouldContain("FAILED");
    }

    [Fact]
    public void Build_WithUnresolvedQuestions_ListsThem()
    {
        var description = PullRequestDescriptionBuilder.Build(
            EvidenceWith(questions: ["Should cancelled orders be included?"]), Plan, []);

        description.ShouldContain("Should cancelled orders be included?");
    }

    private static TaskEvidence EvidenceWith(
        IReadOnlyList<CheckResult>? runtimeChecks = null,
        IReadOnlyList<DomainCheckResult>? domainChecks = null,
        IReadOnlyList<string>? questions = null) => new(
        runtimeChecks ?? [],
        domainChecks ?? [],
        new GitDiffSummary("abc", "def", ["src/Orders.cs"], null),
        "sha256:abc123",
        questions ?? []);

    private static CheckResult Check(string command, bool passed) => new(
        command, passed, passed ? 0 : 1, TimeSpan.FromMinutes(1), "artifact-1", CheckOrigin.RuntimeExecuted);

    private static DomainCheckResult DomainCheck(
        string environment, string company, string queryName, bool passed) => new(
        queryName, environment, company, passed, [], $"{queryName} against {company}");

    private static PlanRevision RevisionWithHash(string hash) => new(
        Guid.CreateVersion7(),
        1,
        new PlanContent(
            "Add ordering endpoint", ["api"], [], "Add a controller plus tests",
            [], ["dotnet test"], [], [],
            new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3), [], []));

    private static string Section(string description, string heading)
    {
        var start = description.IndexOf("## " + heading, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        var next = description.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return next < 0 ? description[start..] : description[start..next];
    }
}
