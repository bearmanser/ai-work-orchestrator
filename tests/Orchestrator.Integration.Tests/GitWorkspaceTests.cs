using LibGit2Sharp;
using Orchestrator.Adapters.Git;
using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Secrets;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class GitWorkspaceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Signature TestSignature = new(
        "orchestrator-test", "test@example.com",
        new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));

    private string _root = null!;
    private string _originPath = null!;
    private LibGit2Workspace _workspace = null!;

    public Task InitializeAsync()
    {
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "git-workspace-tests-" + Guid.NewGuid().ToString("N"))).FullName;

        var seedPath = Path.Combine(_root, "seed");
        Repository.Init(seedPath);
        using (var seed = new Repository(seedPath))
        {
            File.WriteAllText(Path.Combine(seedPath, "README.md"), "seed");
            Commands.Stage(seed, "*");
            seed.Commit("initial commit", TestSignature, TestSignature);
            seed.Branches.Rename("master", "main");
        }

        _originPath = Path.Combine(_root, "origin.git");
        Repository.Clone(seedPath, _originPath, new CloneOptions { IsBare = true });
        DeleteDirectory(seedPath);

        _workspace = new LibGit2Workspace(
            Path.Combine(_root, "git"),
            new FileSecretStore(Path.Combine(_root, "secrets")),
            TimeProvider.System,
            new EfWorkspaceStore(fixture.NewDbContext()));

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            DeleteDirectory(_root);
        return Task.CompletedTask;
    }

    /// <summary>Git leaves read-only files behind; clear them before deleting.</summary>
    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    [Fact]
    public async Task Prepare_CreatesIsolatedCheckoutOnANewBranch()
    {
        var handle = await _workspace.PrepareAsync(
            Request(baseBranch: "main", workBranch: "orchestrator/task-1"), default);

        Directory.Exists(handle.Path).ShouldBeTrue();
        handle.BaselineCommit.ShouldBe(await HeadOfAsync(_originPath, "main"));
        (await CurrentBranchAsync(handle.Path)).ShouldBe("orchestrator/task-1");
    }

    [Fact]
    public async Task Prepare_TwoTasksSameRepository_GetSeparateDirectories()
    {
        var repositoryId = Guid.CreateVersion7();
        var a = await _workspace.PrepareAsync(
            Request(taskId: Guid.CreateVersion7(), repositoryId: repositoryId, workBranch: "orchestrator/a"), default);
        var b = await _workspace.PrepareAsync(
            Request(taskId: Guid.CreateVersion7(), repositoryId: repositoryId, workBranch: "orchestrator/b"), default);

        a.Path.ShouldNotBe(b.Path);
        await File.WriteAllTextAsync(Path.Combine(a.Path, "only-in-a.txt"), "x");
        File.Exists(Path.Combine(b.Path, "only-in-a.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task Prepare_NeverTouchesAnExistingDeveloperCheckout()
    {
        // The orchestrator clones into its own root; it must not be pointed at a working copy.
        var developerCheckout = await CloneToTempAsync(_originPath);
        await File.WriteAllTextAsync(Path.Combine(developerCheckout, "uncommitted.txt"), "work in progress");

        await _workspace.PrepareAsync(Request(), default);

        (await File.ReadAllTextAsync(Path.Combine(developerCheckout, "uncommitted.txt")))
            .ShouldBe("work in progress");
    }

    [Fact]
    public async Task CaptureDiff_ReportsChangedFilesAndUnifiedDiff()
    {
        var handle = await _workspace.PrepareAsync(Request(), default);
        Directory.CreateDirectory(Path.Combine(handle.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(handle.Path, "src", "New.cs"), "public class New;");
        await CommitAllAsync(handle.Path, "add New.cs");

        var diff = await _workspace.CaptureDiffAsync(handle.WorkspaceId, default);

        diff.Files.Select(f => f.Path).ShouldContain("src/New.cs");
        diff.UnifiedDiff.ShouldContain("public class New;");
        diff.BaselineCommit.ShouldBe(handle.BaselineCommit);
    }

    [Fact]
    public async Task HasBaselineDrifted_AfterSomeoneElsePushesToBase_IsTrue()
    {
        var handle = await _workspace.PrepareAsync(Request(baseBranch: "main"), default);
        await PushUnrelatedCommitAsync(_originPath, "main");

        (await _workspace.HasBaselineDriftedAsync(handle.WorkspaceId, default)).ShouldBeTrue();
    }

    [Fact]
    public async Task HasBaselineDrifted_WhenBaseUnchanged_IsFalse()
    {
        var handle = await _workspace.PrepareAsync(Request(baseBranch: "main"), default);
        (await _workspace.HasBaselineDriftedAsync(handle.WorkspaceId, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_RemovesTheWorkspaceDirectory()
    {
        var handle = await _workspace.PrepareAsync(Request(), default);
        await _workspace.DisposeAsync(handle.WorkspaceId, default);
        Directory.Exists(handle.Path).ShouldBeFalse();
    }

    private WorkspaceRequest Request(
        Guid? taskId = null,
        Guid? repositoryId = null,
        string? baseBranch = null,
        string? workBranch = null) => new(
        TaskId: taskId ?? Guid.CreateVersion7(),
        RepositoryId: repositoryId ?? Guid.CreateVersion7(),
        RemoteUrl: _originPath,
        BaseBranch: baseBranch ?? "main",
        WorkBranch: workBranch ?? $"orchestrator/task-{Guid.NewGuid():N}",
        CredentialRef: new SecretRef("test.unused"));

    private static Task<string> HeadOfAsync(string repoPath, string branch)
    {
        using var repo = new Repository(repoPath);
        var tip = repo.Branches[branch]?.Tip
            ?? throw new InvalidOperationException($"Branch '{branch}' not found in '{repoPath}'.");
        return Task.FromResult(tip.Sha);
    }

    private static Task<string> CurrentBranchAsync(string workdir)
    {
        using var repo = new Repository(workdir);
        return Task.FromResult(repo.Head.FriendlyName);
    }

    private static async Task<string> CloneToTempAsync(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), "git-workspace-dev-" + Guid.NewGuid().ToString("N"));
        await Task.Run(() => Repository.Clone(source, path));
        return path;
    }

    private static async Task CommitAllAsync(string workdir, string message)
    {
        await Task.Run(() =>
        {
            using var repo = new Repository(workdir);
            Commands.Stage(repo, "*");
            repo.Commit(message, TestSignature, TestSignature);
        });
    }

    private static async Task PushUnrelatedCommitAsync(string originPath, string branch)
    {
        var clonePath = await CloneToTempAsync(originPath);
        try
        {
            await Task.Run(() =>
            {
                using var repo = new Repository(clonePath);
                Commands.Checkout(repo, branch);
                File.WriteAllText(
                    Path.Combine(clonePath, $"unrelated-{Guid.NewGuid():N}.txt"), "unrelated");
                Commands.Stage(repo, "*");
                repo.Commit("unrelated change", TestSignature, TestSignature);
                var remote = repo.Network.Remotes["origin"]
                    ?? throw new InvalidOperationException("Clone has no origin remote.");
                repo.Network.Push(remote, @"refs/heads/" + branch, new PushOptions());
            });
        }
        finally
        {
            DeleteDirectory(clonePath);
        }
    }
}
