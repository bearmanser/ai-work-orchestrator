using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Shouldly;

namespace Orchestrator.Domain.Tests;

public class AuthorizationEnvelopeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T10:00:00Z");
    private static readonly Guid RepoA = Guid.CreateVersion7();
    private static readonly Guid RepoB = Guid.CreateVersion7();

    private static AuthorizationEnvelope Envelope() => new(
        taskId: Guid.CreateVersion7(),
        requesterId: "U1",
        repositories: [new RepositoryScope(RepoA, ["main", "feature/*"], ["src/"], RepositoryAccess.Write)],
        p1Environments: ["staging"],
        p1Companies: ["KAMELEON-TEST"],
        allowedProviders: ["claude"],
        allowedStages: [AgentStage.Planning, AgentStage.Implementation],
        slackChannels: ["C0ENG"],
        contactablePeople: ["U2"],
        vaultPathPrefixes: ["Projects/Orchestrator/"],
        networkDestinations: ["api.anthropic.com"],
        budget: new BudgetLimits(20m, TimeSpan.FromHours(4), 8, 3),
        expiresAt: Now.AddDays(7),
        createdAt: Now);

    [Fact]
    public void Covers_WriteToScopedRepositoryAndBranch_IsTrue() =>
        Envelope().Covers(ActionKind.RepositoryWrite,
            ResourceScope.Repository(RepoA, "feature/orders"), Now).ShouldBeTrue();

    [Fact]
    public void Covers_UnlistedRepository_IsFalse() =>
        Envelope().Covers(ActionKind.RepositoryRead,
            ResourceScope.Repository(RepoB, "main"), Now).ShouldBeFalse();

    [Fact]
    public void Covers_UnlistedBranch_IsFalse() =>
        Envelope().Covers(ActionKind.RepositoryWrite,
            ResourceScope.Repository(RepoA, "release/2026"), Now).ShouldBeFalse();

    [Fact]
    public void Covers_ProductionP1_IsFalse() =>
        Envelope().Covers(ActionKind.DomainRead,
            ResourceScope.P1("production", "KAMELEON-TEST"), Now).ShouldBeFalse();

    [Fact]
    public void Covers_WrongCompany_IsFalse() =>
        Envelope().Covers(ActionKind.DomainRead,
            ResourceScope.P1("staging", "KAMELEON-LIVE"), Now).ShouldBeFalse();

    [Fact]
    public void Covers_UnauthorizedProvider_IsFalse() =>
        Envelope().Covers(ActionKind.AgentInvoke,
            ResourceScope.Agent("codex", AgentStage.Implementation), Now).ShouldBeFalse();

    [Fact]
    public void Covers_UnauthorizedStage_IsFalse() =>
        Envelope().Covers(ActionKind.AgentInvoke,
            ResourceScope.Agent("claude", AgentStage.Review), Now).ShouldBeFalse();

    [Fact]
    public void Covers_VaultPathOutsidePrefix_IsFalse() =>
        Envelope().Covers(ActionKind.MemoryWrite,
            ResourceScope.VaultNote("Personal/Diary.md"), Now).ShouldBeFalse();

    [Fact]
    public void Covers_AfterExpiry_IsFalse() =>
        Envelope().Covers(ActionKind.RepositoryRead,
            ResourceScope.Repository(RepoA, "main"), Now.AddDays(8)).ShouldBeFalse();

    [Fact]
    public void Covers_AfterRevocation_IsFalse()
    {
        var envelope = Envelope();
        envelope.Revoke(Now.AddHours(1), "operator revoked");
        envelope.Covers(ActionKind.RepositoryRead,
            ResourceScope.Repository(RepoA, "main"), Now.AddHours(2)).ShouldBeFalse();
    }

    [Fact]
    public void Covers_ContactingUnlistedPerson_IsFalse() =>
        Envelope().Covers(ActionKind.ContactPerson, ResourceScope.Person("U9"), Now).ShouldBeFalse();

    // --- Fix round 1: a read-only repository grant must not cover any action that
    // mutates the repository, even one that "only" adds a comment. ---

    private static AuthorizationEnvelope ReadOnlyEnvelope() => new(
        taskId: Guid.CreateVersion7(),
        requesterId: "U1",
        repositories: [new RepositoryScope(RepoA, ["main", "feature/*"], ["src/"], RepositoryAccess.Read)],
        p1Environments: ["staging"],
        p1Companies: ["KAMELEON-TEST"],
        allowedProviders: ["claude"],
        allowedStages: [AgentStage.Planning, AgentStage.Implementation],
        slackChannels: ["C0ENG"],
        contactablePeople: ["U2"],
        vaultPathPrefixes: ["Projects/Orchestrator/"],
        networkDestinations: ["api.anthropic.com"],
        budget: new BudgetLimits(20m, TimeSpan.FromHours(4), 8, 3),
        expiresAt: Now.AddDays(7),
        createdAt: Now);

    [Fact]
    public void Covers_ReadOnlyRepository_DoesNotCoverRepositoryWrite() =>
        ReadOnlyEnvelope().Covers(ActionKind.RepositoryWrite,
            ResourceScope.Repository(RepoA, "main"), Now).ShouldBeFalse();

    [Fact]
    public void Covers_ReadOnlyRepository_DoesNotCoverPullRequestCreate() =>
        ReadOnlyEnvelope().Covers(ActionKind.PullRequestCreate,
            ResourceScope.Repository(RepoA, "main"), Now).ShouldBeFalse();

    [Fact]
    public void Covers_ReadOnlyRepository_DoesNotCoverPullRequestComment() =>
        ReadOnlyEnvelope().Covers(ActionKind.PullRequestComment,
            ResourceScope.Repository(RepoA, "main"), Now).ShouldBeFalse();

    [Fact]
    public void Covers_ReadOnlyRepository_StillCoversRepositoryRead() =>
        ReadOnlyEnvelope().Covers(ActionKind.RepositoryRead,
            ResourceScope.Repository(RepoA, "main"), Now).ShouldBeTrue();

    // --- Fix round 1: IReadOnlyList<T> is only a view. A caller holding the original
    // List<T> it passed into the constructor must not be able to mutate the envelope
    // by mutating that list afterwards. ---

    [Fact]
    public void Constructor_MutatingCallersOriginalList_DoesNotAffectCoversDecision()
    {
        var repoId = Guid.CreateVersion7();
        var branches = new List<string> { "main" };
        var repositories = new List<RepositoryScope>
        {
            new(repoId, branches, ["src/"], RepositoryAccess.Write),
        };

        var envelope = new AuthorizationEnvelope(
            taskId: Guid.CreateVersion7(),
            requesterId: "U1",
            repositories: repositories,
            p1Environments: [],
            p1Companies: [],
            allowedProviders: [],
            allowedStages: [],
            slackChannels: [],
            contactablePeople: [],
            vaultPathPrefixes: [],
            networkDestinations: [],
            budget: new BudgetLimits(1m, TimeSpan.FromHours(1), 1, 1),
            expiresAt: Now.AddDays(1),
            createdAt: Now);

        // Mutate the caller's own references after the envelope was built: add a
        // branch to the nested list, and add a whole new repository scope to the
        // top-level list.
        branches.Add("feature/*");
        repositories.Add(new RepositoryScope(Guid.CreateVersion7(), ["*"], [], RepositoryAccess.Write));

        envelope.Repositories.Count.ShouldBe(1);
        envelope.Covers(ActionKind.RepositoryWrite,
            ResourceScope.Repository(repoId, "feature/orders"), Now).ShouldBeFalse();
    }
}
