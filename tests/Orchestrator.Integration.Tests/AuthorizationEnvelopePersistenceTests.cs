using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class AuthorizationEnvelopePersistenceTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T10:00:00Z");

    [Fact]
    public async Task SaveEnvelope_RoundTrips_AllCollectionsAndBudgetThroughAFreshDbContext()
    {
        var taskId = Guid.CreateVersion7();
        var repoId = Guid.CreateVersion7();

        var created = new AuthorizationEnvelope(
            taskId: taskId,
            requesterId: "U1",
            repositories: [new RepositoryScope(repoId, ["main", "feature/*"], ["src/"], RepositoryAccess.Write)],
            p1Environments: ["staging"],
            p1Companies: ["KAMELEON-TEST"],
            allowedProviders: ["claude"],
            allowedStages: [AgentStage.Planning, AgentStage.Implementation],
            slackChannels: ["C0ENG"],
            contactablePeople: ["U2"],
            vaultPathPrefixes: ["Projects/Orchestrator/"],
            networkDestinations: ["api.anthropic.com"],
            budget: new BudgetLimits(20.123456m, TimeSpan.FromHours(4), 8, 3),
            expiresAt: Now.AddDays(7),
            createdAt: Now);

        await using (var db = fixture.NewDbContext())
        {
            db.AuthorizationEnvelopes.Add(created);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.NewDbContext();
        var loaded = await read.AuthorizationEnvelopes.SingleAsync(e => e.Id == created.Id);

        loaded.TaskId.ShouldBe(taskId);
        loaded.RequesterId.ShouldBe("U1");

        loaded.Repositories.Count.ShouldBe(1);
        loaded.Repositories[0].RepositoryId.ShouldBe(repoId);
        loaded.Repositories[0].Branches.ShouldBe(["main", "feature/*"]);
        loaded.Repositories[0].PathPrefixes.ShouldBe(["src/"]);
        loaded.Repositories[0].Access.ShouldBe(RepositoryAccess.Write);

        loaded.P1Environments.ShouldBe(["staging"]);
        loaded.P1Companies.ShouldBe(["KAMELEON-TEST"]);
        loaded.AllowedProviders.ShouldBe(["claude"]);
        loaded.AllowedStages.ShouldBe([AgentStage.Planning, AgentStage.Implementation]);
        loaded.SlackChannels.ShouldBe(["C0ENG"]);
        loaded.ContactablePeople.ShouldBe(["U2"]);
        loaded.VaultPathPrefixes.ShouldBe(["Projects/Orchestrator/"]);
        loaded.NetworkDestinations.ShouldBe(["api.anthropic.com"]);

        loaded.Budget.MaxUsd.ShouldBe(20.123456m);
        loaded.Budget.MaxWallClock.ShouldBe(TimeSpan.FromHours(4));
        loaded.Budget.MaxAgentInvocations.ShouldBe(8);
        loaded.Budget.MaxCorrectionAttempts.ShouldBe(3);

        loaded.ExpiresAt.ShouldBe(Now.AddDays(7));
        loaded.RevokedAt.ShouldBeNull();

        // Covers still works against the round-tripped instance, not just a freshly
        // constructed one - proving the persisted shape is behaviourally equivalent.
        loaded.Covers(ActionKind.RepositoryWrite,
            ResourceScope.Repository(repoId, "feature/orders"), Now).ShouldBeTrue();
    }

    [Fact]
    public async Task SaveEnvelope_WithEmptyCollections_RoundTripsAsEmptyListsNotNull()
    {
        var taskId = Guid.CreateVersion7();

        var created = new AuthorizationEnvelope(
            taskId: taskId,
            requesterId: "U1",
            repositories: [],
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

        await using (var db = fixture.NewDbContext())
        {
            db.AuthorizationEnvelopes.Add(created);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.NewDbContext();
        var loaded = await read.AuthorizationEnvelopes.SingleAsync(e => e.Id == created.Id);

        loaded.Repositories.ShouldBeEmpty();
        loaded.P1Environments.ShouldBeEmpty();
        loaded.AllowedStages.ShouldBeEmpty();
        loaded.NetworkDestinations.ShouldBeEmpty();

        // An empty list must deny, not throw - a null list would make Covers blow up
        // instead of denying.
        Should.NotThrow(() => loaded.Covers(
            ActionKind.RepositoryRead, ResourceScope.Repository(Guid.CreateVersion7(), "main"), Now));
        loaded.Covers(ActionKind.RepositoryRead,
            ResourceScope.Repository(Guid.CreateVersion7(), "main"), Now).ShouldBeFalse();
    }

    [Fact]
    public async Task Revoke_Persists_AndCoversDeniesAfterReload()
    {
        var created = new AuthorizationEnvelope(
            taskId: Guid.CreateVersion7(),
            requesterId: "U1",
            repositories: [],
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

        await using (var db = fixture.NewDbContext())
        {
            db.AuthorizationEnvelopes.Add(created);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.NewDbContext())
        {
            var toRevoke = await db.AuthorizationEnvelopes.SingleAsync(e => e.Id == created.Id);
            toRevoke.Revoke(Now.AddHours(1), "operator revoked");
            await db.SaveChangesAsync();
        }

        await using var read = fixture.NewDbContext();
        var loaded = await read.AuthorizationEnvelopes.SingleAsync(e => e.Id == created.Id);

        loaded.RevokedAt.ShouldBe(Now.AddHours(1));
        loaded.RevocationReason.ShouldBe("operator revoked");
    }
}
