namespace Orchestrator.Application.Ports;

public sealed record WorkspaceRequest(
    Guid TaskId,
    Guid RepositoryId,
    string RemoteUrl,
    string BaseBranch,
    string WorkBranch,
    SecretRef CredentialRef);

public sealed record WorkspaceHandle(
    string WorkspaceId,
    string Path,
    string BaselineCommit,
    string BaseBranch,
    string WorkBranch);

public sealed record ChangedFile(string Path, string Status);

public sealed record GitDiff(
    string BaselineCommit,
    string HeadCommit,
    IReadOnlyList<ChangedFile> Files,
    string UnifiedDiff);

/// <summary>
/// Isolated per-task repository checkouts. The orchestrator owns the entire
/// directory tree under its git root; no developer working copy is involved.
/// </summary>
public interface IGitWorkspace
{
    Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct);
    Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct);
    Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct);
    Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct);
    Task DisposeAsync(string workspaceId, CancellationToken ct);
}
