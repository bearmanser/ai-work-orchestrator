using Orchestrator.Application.Ports;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// Decorates the real <see cref="IGitWorkspace"/> (<c>LibGit2Workspace</c>) so
/// that <see cref="AcceptanceHost"/>'s repository can have a git remote that is
/// simultaneously (a) a real, parseable
/// <c>https://dev.azure.com/{org}/{project}/_git/{repo}</c> URL - required by
/// <c>PullRequestCreationStepHandler.TryParseRemote</c> - and (b) an actually
/// clonable/pushable local bare repository, with no real network access and no
/// fake git-smart-HTTP server.
///
/// An earlier version of this host tried to get the same effect from git's own
/// <c>url.&lt;base&gt;.insteadOf</c> config globally. That turned out to be the
/// wrong tool: git applies <c>insteadOf</c> when a remote is *added*, not only
/// when it is used, so <c>RepositoryMirror.EnsureRemote</c>'s own
/// "does this mirror still point at the URL we expect?" consistency check
/// (comparing the stored, already-rewritten URL against the literal
/// <c>https://dev.azure.com/...</c> string) failed on every call after the
/// first - which meant it broke exactly the two-tasks-one-repository
/// concurrency scenario this suite most needs to prove. Rewriting the URL in
/// .NET before it ever reaches libgit2, once, keeps every later read of the
/// mirror's stored remote consistent with what production code compares it to.
/// </summary>
public sealed class RemoteRewritingGitWorkspace(IGitWorkspace inner, string fakeAzureDevOpsUrl, string localOriginPath) : IGitWorkspace
{
    public Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct) =>
        inner.PrepareAsync(request with { RemoteUrl = Rewrite(request.RemoteUrl) }, ct);

    public Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct) =>
        inner.CaptureDiffAsync(workspaceId, ct);

    public Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct) =>
        inner.PushAsync(workspaceId, credentialRef, ct);

    public Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct) =>
        inner.HasBaselineDriftedAsync(workspaceId, ct);

    public Task DisposeAsync(string workspaceId, CancellationToken ct) =>
        inner.DisposeAsync(workspaceId, ct);

    private string Rewrite(string remoteUrl) =>
        remoteUrl == fakeAzureDevOpsUrl ? localOriginPath : remoteUrl;
}
