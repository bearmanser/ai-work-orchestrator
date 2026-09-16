using System.Security.Cryptography;
using System.Text;
using Orchestrator.Adapters.Obsidian;
using Orchestrator.Application.Ports;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Trait("Category", "Integration")]
public sealed class FileSystemVaultStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _vaultRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "vault-store-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _indexDirectory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "vault-index-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly FileSystemVaultStore _store;

    public FileSystemVaultStoreTests()
    {
        _store = new FileSystemVaultStore(_vaultRoot, _indexDirectory, TimeProvider.System);
    }

    [Fact]
    public async Task Write_NewNote_EmitsTheSixPropertiesInOrder()
    {
        await _store.WriteAsync(Write("Projects/Orchestrator/Decisions.md",
            Frontmatter("Decisions", "reference", "engineering", "active", Now, ["orchestrator"])), default);

        var lines = (await File.ReadAllLinesAsync(PathFor("Projects/Orchestrator/Decisions.md"))).Take(9).ToArray();
        lines[0].ShouldBe("---");
        lines[1].ShouldStartWith("title:");
        lines[2].ShouldStartWith("type:");
        lines[3].ShouldStartWith("area:");
        lines[4].ShouldStartWith("status:");
        lines[5].ShouldStartWith("updated:");
        lines[6].ShouldStartWith("tags:");
    }

    [Fact]
    public async Task Write_MissingARequiredProperty_Throws() =>
        await Should.ThrowAsync<ArgumentException>(() => _store.WriteAsync(
            Write("Projects/X.md", FrontmatterWithout("area")), default));

    [Fact]
    public async Task Write_WithStaleContentHash_ReturnsConflictAndLeavesTheFileUntouched()
    {
        await SeedNoteAsync("Projects/Orchestrator/Decisions.md", "original body");
        const string stale = "sha256:0000";

        var result = await _store.WriteAsync(
            Write("Projects/Orchestrator/Decisions.md", Frontmatter(), "replacement", stale), default);

        result.ShouldBe(VaultWriteResult.Conflict);
        (await File.ReadAllTextAsync(PathFor("Projects/Orchestrator/Decisions.md"))).ShouldContain("original body");
    }

    [Fact]
    public async Task Write_PreservesUnrelatedSectionsWhenUpdatingOne()
    {
        await SeedNoteAsync("Projects/Orchestrator/Decisions.md",
            "## Keep me\ncontent\n\n## Replace me\nold");

        await _store.AppendOrReplaceSectionAsync("Projects/Orchestrator/Decisions.md", "Replace me", "new", default);

        var text = await File.ReadAllTextAsync(PathFor("Projects/Orchestrator/Decisions.md"));
        text.ShouldContain("## Keep me");
        text.ShouldContain("content");
        text.ShouldContain("new");
        text.ShouldNotContain("old");
    }

    [Fact]
    public async Task Read_PathOutsideTheVaultRoot_Throws() =>
        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _store.ReadAsync("../../../etc/passwd", default));

    [Fact]
    public async Task Search_FiltersByPathPrefixBeforeReturning()
    {
        await SeedNoteAsync("Projects/Orchestrator/A.md", "orders");
        await SeedNoteAsync("HR/Salaries.md", "orders");

        var notes = await _store.SearchAsync(
            new VaultQuery("orders", AllowedPathPrefixes: ["Projects/"]), default);

        notes.Select(n => n.Path).ShouldBe(["Projects/Orchestrator/A.md"]);
    }

    [Fact]
    public async Task Search_RestrictedNote_IsNotEvenRead()
    {
        // Access filtering happens before retrieval, so there is nothing to redact afterwards.
        await SeedNoteAsync("HR/Salaries.md", "secret numbers");
        var notes = await _store.SearchAsync(new VaultQuery("secret", ["Projects/"]), default);

        notes.ShouldBeEmpty();
        _store.FilesRead.ShouldNotContain(p => p.Contains("HR/"));
    }

    [Fact]
    public async Task Write_RecordsAReversibleRevision()
    {
        await SeedNoteAsync("Projects/Orchestrator/A.md", "v1");
        await _store.WriteAsync(Write("Projects/Orchestrator/A.md", Frontmatter(), "v2", HashOf("v1")), default);

        var revisions = await _store.GetRevisionsAsync("Projects/Orchestrator/A.md", default);
        revisions.Count.ShouldBe(2);
        await _store.RevertAsync("Projects/Orchestrator/A.md", revisions[0].RevisionId, default);
        (await File.ReadAllTextAsync(PathFor("Projects/Orchestrator/A.md"))).ShouldContain("v1");
    }

    public void Dispose()
    {
        Directory.Delete(_vaultRoot, recursive: true);
        Directory.Delete(_indexDirectory, recursive: true);
    }

    private static NoteFrontmatter Frontmatter(
        string title = "Note",
        string type = "reference",
        string area = "engineering",
        string status = "active",
        DateTimeOffset? updated = null,
        string[]? tags = null) =>
        new(title, type, area, status, updated ?? Now, tags ?? ["orchestrator"]);

    private static NoteFrontmatter FrontmatterWithout(string property) => property switch
    {
        "title" => Frontmatter(title: string.Empty),
        "type" => Frontmatter(type: string.Empty),
        "area" => Frontmatter(area: string.Empty),
        "status" => Frontmatter(status: string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
    };

    private static VaultWrite Write(string path, NoteFrontmatter frontmatter, string body = "body", string? expectedHash = null) =>
        new(path, frontmatter, body, expectedHash);

    private Task SeedNoteAsync(string path, string body) =>
        _store.WriteAsync(Write(path, Frontmatter(), body), default);

    private string PathFor(string relativePath) =>
        Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string HashOf(string body) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}
