using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Context;
using Orchestrator.Domain.Evidence;
using Orchestrator.Infrastructure.Artifacts;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class ArtifactStoreTests(PostgresFixture fixture) : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "artifact-store-tests-" + Guid.NewGuid())).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Put_ThenOpen_RoundTripsContentAndRecordsHash()
    {
        var taskId = Guid.CreateVersion7();
        var store = new FileSystemArtifactStore(_root, fixture.NewDbContext(), TimeProvider.System);
        var bytes = "diff --git a/x b/x"u8.ToArray();

        var id = await store.PutAsync(
            new ArtifactDescriptor(taskId, "diff", "text/plain", ArtifactSensitivity.Internal, "git-adapter"),
            new MemoryStream(bytes), default);

        await using var stream = await store.OpenAsync(id, default);
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).ShouldBe("diff --git a/x b/x");

        await using var db = fixture.NewDbContext();
        var artifact = await db.Artifacts.SingleAsync(a => a.Id == id);
        artifact.ContentHash.ShouldStartWith("sha256:");
        artifact.TaskId.ShouldBe(taskId);
    }

    [Fact]
    public async Task Open_TamperedFile_ThrowsIntegrityException()
    {
        var taskId = Guid.CreateVersion7();
        var store = new FileSystemArtifactStore(_root, fixture.NewDbContext(), TimeProvider.System);
        var id = await store.PutAsync(
            new ArtifactDescriptor(taskId, "log", "text/plain", ArtifactSensitivity.Internal, "test"),
            new MemoryStream("ok"u8.ToArray()), default);

        await File.WriteAllTextAsync(PathFor(taskId, id), "tampered");

        await Should.ThrowAsync<ArtifactIntegrityException>(() => store.OpenAsync(id, default));
    }

    [Fact]
    public async Task Open_UnknownId_ThrowsNotFound()
    {
        var store = new FileSystemArtifactStore(_root, fixture.NewDbContext(), TimeProvider.System);

        await Should.ThrowAsync<ArtifactNotFoundException>(
            () => store.OpenAsync(Guid.CreateVersion7().ToString("N"), default));
    }

    [Fact]
    public async Task SaveSnapshot_RoundTrips_SourcesAndOmissions()
    {
        var taskId = Guid.CreateVersion7();
        var snapshot = new ContextSnapshot(
            taskId,
            Guid.CreateVersion7().ToString("N"),
            [new ContextSource("repository", "repo-a", "abc123", "sha256:deadbeef")],
            DateTimeOffset.Parse("2026-09-16T08:00:00Z"),
            "repo-a:read",
            8000,
            ["vault:secret-note"]);

        await using (var db = fixture.NewDbContext())
        {
            db.ContextSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.NewDbContext();
        var loaded = await read.ContextSnapshots.SingleAsync(s => s.Id == snapshot.Id);
        loaded.TaskId.ShouldBe(taskId);
        loaded.TokenBudget.ShouldBe(8000);
        loaded.RetrievedAt.Offset.ShouldBe(TimeSpan.Zero);

        var sources = JsonSerializer.Deserialize<List<ContextSource>>(loaded.SourcesJson);
        sources.ShouldNotBeNull();
        sources.Count.ShouldBe(1);
        sources[0].Identifier.ShouldBe("repo-a");

        var omitted = JsonSerializer.Deserialize<List<string>>(loaded.OmittedSourcesJson);
        omitted.ShouldBe(["vault:secret-note"]);
    }

    private string PathFor(Guid taskId, string artifactId) =>
        Path.Combine(_root, taskId.ToString("N"), artifactId + ".bin");
}
