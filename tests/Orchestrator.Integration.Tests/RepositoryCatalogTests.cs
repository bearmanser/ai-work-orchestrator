using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Repositories;
using Orchestrator.Infrastructure.Audit;
using Orchestrator.Infrastructure.Persistence;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class RepositoryCatalogTests(PostgresFixture fixture)
{
    private readonly string _remoteSuffix = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Create_ValidProfile_IsPersistedAndAudited()
    {
        var created = await Service().CreateAsync(new CreateRepositoryRequest(
            Name: "kameleon-backend",
            RemoteUrl: $"https://dev.azure.com/kameleon/Platform/_git/backend-{_remoteSuffix}",
            DefaultBranch: "main",
            Purpose: "P1 domain API",
            OwnerContactId: "U-owner",
            AccessPolicy: RepositoryAccess.Write,
            BuildCommands: new BuildCommands("dotnet restore", "dotnet build", "dotnet test", null, ".", TimeSpan.FromMinutes(20)),
            InstructionFilePaths: ["CLAUDE.md"]), "magnus", default);

        await using var db = fixture.NewDbContext();
        (await db.Repositories.SingleAsync(r => r.Id == created.Id)).Name.ShouldBe("kameleon-backend");
        (await db.AuditEvents.AnyAsync(e => e.Action == "repository.create" && e.Actor == "magnus")).ShouldBeTrue();
    }

    [Fact]
    public async Task Update_WithStaleVersion_IsRejected()
    {
        var created = await SeedRepositoryAsync();
        await Service().UpdateAsync(created.Id, RenameRequest("first"), created.Version, "magnus", default);

        await Should.ThrowAsync<ConcurrentModificationException>(
            () => Service().UpdateAsync(created.Id, RenameRequest("second"), created.Version, "magnus", default));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ssh://somewhere-else.example.com/repo.git")]
    public async Task Create_RemoteOutsideConfiguredHost_IsRejected(string remote) =>
        await Should.ThrowAsync<ArgumentException>(
            () => Service().CreateAsync(RequestWithRemote(remote), "magnus", default));

    [Fact]
    public async Task Create_DuplicateRemote_IsRejected()
    {
        var request = RequestWithRemote($"https://dev.azure.com/kameleon/Platform/_git/backend-{_remoteSuffix}");
        await Service().CreateAsync(request, "magnus", default);
        await Should.ThrowAsync<InvalidOperationException>(() => Service().CreateAsync(request, "magnus", default));
    }

    [Fact]
    public async Task ListEndpoint_WithoutAuthentication_Returns401()
    {
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        using var client = new OrchestratorApiFactory().CreateClient();

        (await client.GetAsync("/api/admin/repositories")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Update_ValidVersion_PersistsChangesAndAudits()
    {
        var created = await SeedRepositoryAsync();
        var updated = await Service().UpdateAsync(created.Id, RenameRequest("renamed"), created.Version, "magnus", default);

        updated.Name.ShouldBe("renamed");
        await using var db = fixture.NewDbContext();
        (await db.Repositories.SingleAsync(r => r.Id == created.Id)).Name.ShouldBe("renamed");
        (await db.AuditEvents.AnyAsync(e => e.Action == "repository.update")).ShouldBeTrue();
    }

    private RepositoryCatalogService Service() => new(
        new EfRepositoryProfileStore(fixture.NewDbContext()),
        new AuditWriter(fixture.NewDbContext(), TimeProvider.System),
        new GitCatalogOptions { AllowedHosts = ["dev.azure.com", "kameleon.visualstudio.com"] });

    private async Task<RepositoryProfile> SeedRepositoryAsync()
    {
        var created = await Service().CreateAsync(RequestWithRemote(
            $"https://dev.azure.com/kameleon/Platform/_git/seed-{_remoteSuffix}"), "magnus", default);
        await using var db = fixture.NewDbContext();
        return await db.Repositories.SingleAsync(r => r.Id == created.Id);
    }

    private CreateRepositoryRequest RequestWithRemote(string remote) => new(
        Name: $"repo-{_remoteSuffix}",
        RemoteUrl: remote,
        DefaultBranch: "main",
        Purpose: "test",
        OwnerContactId: "U-owner",
        AccessPolicy: RepositoryAccess.Read,
        BuildCommands: new BuildCommands(null, null, null, null, ".", TimeSpan.FromMinutes(5)),
        InstructionFilePaths: []);

    private static UpdateRepositoryRequest RenameRequest(string name) => new(
        Name: name,
        Purpose: "test",
        DefaultBranch: "main",
        OwnerContactId: "U-owner",
        BuildCommands: null,
        InstructionFilePaths: []);
}
