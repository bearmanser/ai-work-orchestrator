using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Repositories;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class RelationshipResolverTests
{
    private static readonly RepositoryProfile Frontend = TestProfile("kameleon-web", "web");
    private static readonly RepositoryProfile Backend = TestProfile("kameleon-backend", "backend");
    private static readonly RepositoryProfile Contracts = TestProfile("kameleon-contracts", "contracts");

    private static readonly RelationshipResolver Resolver = new(TimeProvider.System);

    [Fact]
    public async Task Resolve_FrontendBaseUrlMatchingBackendSurface_ProducesEvidencedRelationship()
    {
        var outcome = await Resolver.ResolveAsync(
        [
            (Frontend, InspectionWith(Dependency("http-base-url", "https://backend.internal.kameleon.no",
                                                 "src/Web/appsettings.json"))),
            (Backend, InspectionWith(surfaces: [ApiSurface("https://backend.internal.kameleon.no", "src/Api/Program.cs")])),
        ], default);

        var relationship = outcome.Relationships.Single();
        relationship.SourceRepositoryId.ShouldBe(Frontend.Id);
        relationship.TargetRepositoryId.ShouldBe(Backend.Id);
        relationship.RelationshipType.ShouldBe(RelationshipType.ConsumesHttpApi);
        relationship.Status.ShouldBe(RelationshipStatus.Evidenced);
        relationship.Evidence.Single().SourcePath.ShouldBe("src/Web/appsettings.json");
    }

    [Fact]
    public async Task Resolve_BaseUrlWithNoMatchingRepository_ProducesAnOpenQuestion_NotARelationship()
    {
        var outcome = await Resolver.ResolveAsync(
        [
            (Frontend, InspectionWith(Dependency("http-base-url", "https://unknown.example.com", "src/Web/appsettings.json"))),
        ], default);

        outcome.Relationships.ShouldBeEmpty();
        outcome.Questions.Single().Detail.ShouldContain("https://unknown.example.com");
    }

    [Fact]
    public async Task Resolve_SimilarNamesWithoutSourceEvidence_ProducesNothing()
    {
        var outcome = await Resolver.ResolveAsync(
        [
            (ProfileNamed("kameleon-orders-api"), EmptyInspection),
            (ProfileNamed("kameleon-orders-web"), EmptyInspection),
        ], default);

        outcome.Relationships.ShouldBeEmpty();
        outcome.Questions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolve_SharedNuGetPackageProducedByAnotherRepository_IsSharesLibrary()
    {
        var outcome = await Resolver.ResolveAsync(
        [
            (Frontend, InspectionWith(Dependency("nuget", "Kameleon.Contracts", "src/Web/Web.csproj", "3.1.0"))),
            (Contracts, InspectionWith(projects: [ProducedPackage("Kameleon.Contracts", "src/Contracts/Contracts.csproj")])),
        ], default);

        outcome.Relationships.Single().RelationshipType.ShouldBe(RelationshipType.SharesLibrary);
    }

    [Fact]
    public async Task Resolve_AdminAssertionContradictedByNewEvidence_IsMarkedConflicted()
    {
        var existing = RepositoryRelationship.AssertByAdmin(
            Frontend.Id, Backend.Id, RelationshipType.ConsumesHttpApi,
            "Magnus: they talk over HTTP", DateTimeOffset.Parse("2026-09-16T09:00:00Z"));
        var outcome = await Resolver.ResolveAsync(
            [(Frontend, InspectionWith(Dependency("nuget", "Kameleon.Contracts", "src/Web/Web.csproj", "3.1.0"))),
             (Backend, EmptyInspection)],
            existingRelationships: [existing], default);

        outcome.Relationships.Single(r => r.Id == existing.Id).Status.ShouldBe(RelationshipStatus.Conflicted);
    }

    [Fact]
    public async Task Resolve_AdminAssertionIsNeverSilentlyDeleted()
    {
        var existing = RepositoryRelationship.AssertByAdmin(
            Frontend.Id, Backend.Id, RelationshipType.ConsumesHttpApi,
            "Magnus", DateTimeOffset.Parse("2026-09-16T09:00:00Z"));
        var outcome = await Resolver.ResolveAsync(
            [(Frontend, EmptyInspection), (Backend, EmptyInspection)],
            existingRelationships: [existing], default);

        outcome.Relationships.ShouldContain(r => r.Id == existing.Id);
    }

    [Fact]
    public async Task Resolve_GeneratedClientReferencingBackendSurface_IsGeneratesClientFrom()
    {
        var outcome = await Resolver.ResolveAsync(
        [
            (Frontend, InspectionWith(Dependency("generated-client", "https://backend.internal.kameleon.no/openapi.json",
                                                 "client/backend.nswag"))),
            (Backend, InspectionWith(surfaces: [ApiSurface("https://backend.internal.kameleon.no", "src/Api/Program.cs")])),
        ], default);

        outcome.Relationships.Single().RelationshipType.ShouldBe(RelationshipType.GeneratesClientFrom);
    }

    private static RepositoryProfile TestProfile(string name, string remote) => new(
        name, $"https://dev.azure.com/kameleon/Platform/_git/{remote}", "main",
        "test", "U-owner", RepositoryAccess.Read,
        new BuildCommands(null, null, null, null, ".", TimeSpan.FromMinutes(5)), []);

    private static RepositoryProfile ProfileNamed(string name) => TestProfile(name, name);

    private static InspectionResult InspectionWith(
        DetectedDependency? dependency = null,
        IReadOnlyList<DetectedApiSurface>? surfaces = null,
        IReadOnlyList<DetectedProject>? projects = null) => new(
        projects ?? [], null, [], surfaces ?? [],
        dependency is null ? [] : [dependency]);

    private static DetectedDependency Dependency(string kind, string identifier, string sourcePath, string? version = null) =>
        new(kind, identifier, sourcePath, version);

    private static DetectedApiSurface ApiSurface(string identifier, string path) =>
        new(path, "http", identifier);

    private static DetectedProject ProducedPackage(string name, string path) =>
        new(path, "dotnet", name);

    private static InspectionResult EmptyInspection => new([], null, [], [], []);
}
