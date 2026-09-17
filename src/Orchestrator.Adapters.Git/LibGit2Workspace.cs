using System.Collections.Concurrent;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Adapters.Git;

/// <summary>
/// <see cref="IGitWorkspace"/> backed by LibGit2Sharp. Checkouts are cloned
/// from a local per-repository mirror; credentials are supplied per operation
/// through a handler and never appear in a remote URL or on disk.
/// </summary>
public sealed class LibGit2Workspace(
    string gitRoot,
    ISecretStore secrets,
    TimeProvider clock,
    IWorkspaceStore store) : IGitWorkspace
{
    private readonly string _gitRoot = Path.GetFullPath(gitRoot);

    /// <summary>
    /// One repository's mirror is shared by every task that touches it
    /// (<see cref="WorkspaceLayout.MirrorPath"/> is keyed by repository id
    /// alone), but nothing previously serialized concurrent access to it.
    /// Two tasks preparing a workspace for the same repository at the same
    /// time both fetch into that one mirror, and libgit2 takes an OS-level
    /// lock file per ref during a fetch - the second caller's fetch (or a
    /// concurrent clone reading refs while another fetch is rewriting them)
    /// fails with a real <see cref="LibGit2SharpException"/>
    /// ("failed to lock file ... for writing"), not a graceful queue. This
    /// surfaced in Task 49's acceptance suite (two tasks against one
    /// repository, run concurrently - exactly the scenario the mirror-sharing
    /// design implies must be safe) and is fixed here with one lock per
    /// repository id, scoped process-wide since a mirror is a process-wide
    /// resource regardless of which DI scope's <see cref="LibGit2Workspace"/>
    /// instance touches it.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> MirrorLocks = new();

    private static SemaphoreSlim MirrorLockFor(Guid repositoryId) =>
        MirrorLocks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));

    public async Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RemoteUrl))
            throw new ArgumentException("Remote URL is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.BaseBranch))
            throw new ArgumentException("Base branch is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.WorkBranch))
            throw new ArgumentException("Work branch is required.", nameof(request));

        var mirrorPath = WorkspaceLayout.MirrorPath(_gitRoot, request.RepositoryId);
        var workspacePath = WorkspaceLayout.WorkspacePath(_gitRoot, request.TaskId, request.RepositoryId);
        if (Directory.Exists(workspacePath))
            throw new InvalidOperationException($"Workspace directory '{workspacePath}' already exists.");

        var credentials = await CredentialsForAsync(request.RemoteUrl, request.CredentialRef, ct);

        var mirrorLock = MirrorLockFor(request.RepositoryId);
        await mirrorLock.WaitAsync(ct);
        string baseline;
        try
        {
            baseline = await Task.Run(() =>
            {
                var mirror = new RepositoryMirror(mirrorPath);
                mirror.EnsureRemote(request.RemoteUrl);
                return mirror.FetchBaseTip(request.BaseBranch, credentials);
            }, ct);

            await Task.Run(() => CloneAtBaseline(
                mirrorPath, workspacePath, request, baseline), ct);
        }
        finally
        {
            mirrorLock.Release();
        }

        var workspace = new Workspace(
            request.TaskId, request.RepositoryId, workspacePath, baseline,
            request.BaseBranch, request.WorkBranch, request.CredentialRef.Key,
            clock.GetUtcNow());
        await store.AddAsync(workspace, ct);

        return new WorkspaceHandle(
            workspace.WorkspaceId, workspacePath, baseline,
            request.BaseBranch, request.WorkBranch);
    }

    public async Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct)
    {
        var workspace = await FindAsync(workspaceId, ct);
        return await Task.Run(() =>
        {
            using var repo = new Repository(workspace.Path);
            var head = repo.Head.Tip
                ?? throw new InvalidOperationException($"Workspace '{workspaceId}' has no commits.");
            var baseline = repo.Lookup<Commit>(workspace.BaselineCommit)
                ?? throw new InvalidOperationException(
                    $"Baseline commit '{workspace.BaselineCommit}' was not found in workspace '{workspaceId}'.");
            var patch = repo.Diff.Compare<Patch>(baseline.Tree, head.Tree);
            var files = patch
                .Select(e => new ChangedFile(e.Path, e.Status.ToString()))
                .ToList();
            return new GitDiff(workspace.BaselineCommit, head.Sha, files, patch.Content ?? string.Empty);
        }, ct);
    }

    public async Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct)
    {
        var workspace = await FindAsync(workspaceId, ct);

        string originUrl;
        using (var repo = new Repository(workspace.Path))
        {
            originUrl = repo.Network.Remotes["origin"]?.Url
                ?? throw new InvalidOperationException($"Workspace '{workspaceId}' has no origin remote.");
        }

        var credentials = await CredentialsForAsync(originUrl, credentialRef, ct);

        return await Task.Run(() =>
        {
            using var repo = new Repository(workspace.Path);
            var branch = repo.Branches[workspace.WorkBranch]
                ?? throw new InvalidOperationException(
                    $"Work branch '{workspace.WorkBranch}' was not found in workspace '{workspaceId}'.");

            // Deliberately a non-force push: pushing a new work branch succeeds,
            // while a concurrently created or moved remote branch fails visibly
            // instead of being silently overwritten.
            var remote = repo.Network.Remotes["origin"]
                ?? throw new InvalidOperationException($"Workspace '{workspaceId}' has no origin remote.");
            repo.Network.Push(remote, "refs/heads/" + workspace.WorkBranch,
                new PushOptions { CredentialsProvider = credentials });
            return branch.Tip.Sha;
        }, ct);
    }

    public async Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct)
    {
        var workspace = await FindAsync(workspaceId, ct);
        var mirrorPath = WorkspaceLayout.MirrorPath(_gitRoot, workspace.RepositoryId);
        var mirror = new RepositoryMirror(mirrorPath);
        var credentials = await CredentialsForAsync(
            mirror.RemoteUrl, new SecretRef(workspace.CredentialKey), ct);

        var mirrorLock = MirrorLockFor(workspace.RepositoryId);
        await mirrorLock.WaitAsync(ct);
        string tip;
        try
        {
            tip = await Task.Run(() => mirror.FetchBaseTip(workspace.BaseBranch, credentials), ct);
        }
        finally
        {
            mirrorLock.Release();
        }

        return !tip.Equals(workspace.BaselineCommit, StringComparison.Ordinal);
    }

    public async Task DisposeAsync(string workspaceId, CancellationToken ct)
    {
        var workspace = await FindAsync(workspaceId, ct);
        if (Directory.Exists(workspace.Path))
        {
            // Git object files are read-only; recursive delete fails on
            // Windows unless attributes are cleared first.
            foreach (var file in Directory.EnumerateFiles(
                workspace.Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(workspace.Path, recursive: true);
        }

        await store.RemoveAsync(workspace, ct);
    }

    private static void CloneAtBaseline(
        string mirrorPath, string workspacePath, WorkspaceRequest request, string baseline)
    {
        Repository.Clone(mirrorPath, workspacePath, new CloneOptions { IsBare = false, Checkout = false });

        using var repo = new Repository(workspacePath);
        repo.Network.Remotes.Update("origin", r => r.Url = request.RemoteUrl);

        var baselineCommit = repo.Lookup<Commit>(baseline)
            ?? throw new InvalidOperationException($"Baseline commit '{baseline}' was not found after cloning.");
        if (repo.Branches[request.WorkBranch] is not null)
            throw new InvalidOperationException($"Work branch '{request.WorkBranch}' already exists.");

        var baseBranch = repo.Branches[request.BaseBranch] ?? repo.CreateBranch(request.BaseBranch, baselineCommit);
        Commands.Checkout(repo, baseBranch);
        Commands.Checkout(repo, repo.CreateBranch(request.WorkBranch, baselineCommit));
    }

    /// <summary>
    /// Resolves credentials only for network remotes. Local paths need none, so
    /// tests and local mirrors never touch the secret store.
    /// </summary>
    private async Task<CredentialsHandler?> CredentialsForAsync(
        string remoteUrl, SecretRef credentialRef, CancellationToken ct)
    {
        if (!remoteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        var password = await secrets.GetAsync(credentialRef, ct);
        return (_, _, _) => new UsernamePasswordCredentials { Username = string.Empty, Password = password };
    }

    private async Task<Workspace> FindAsync(string workspaceId, CancellationToken ct) =>
        await store.GetByIdAsync(workspaceId, ct)
            ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' was not found.");
}
