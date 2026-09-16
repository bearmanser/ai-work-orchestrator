namespace Orchestrator.Domain.Repositories;

/// <summary>
/// The durable record of one isolated task checkout. Lets step handlers and
/// recovery reattach to an existing checkout instead of cloning again.
/// </summary>
public sealed class Workspace
{
    private Workspace() { }

    public Workspace(
        Guid taskId, Guid repositoryId, string path, string baselineCommit,
        string baseBranch, string workBranch, string credentialKey, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        if (string.IsNullOrWhiteSpace(baselineCommit))
            throw new ArgumentException("Baseline commit is required.", nameof(baselineCommit));
        if (string.IsNullOrWhiteSpace(baseBranch))
            throw new ArgumentException("Base branch is required.", nameof(baseBranch));
        if (string.IsNullOrWhiteSpace(workBranch))
            throw new ArgumentException("Work branch is required.", nameof(workBranch));
        if (string.IsNullOrWhiteSpace(credentialKey))
            throw new ArgumentException("Credential key is required.", nameof(credentialKey));

        WorkspaceId = Guid.CreateVersion7().ToString("N");
        TaskId = taskId;
        RepositoryId = repositoryId;
        Path = path;
        BaselineCommit = baselineCommit;
        BaseBranch = baseBranch;
        WorkBranch = workBranch;
        CredentialKey = credentialKey;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public string WorkspaceId { get; private set; } = null!;
    public Guid TaskId { get; private set; }
    public Guid RepositoryId { get; private set; }
    public string Path { get; private set; } = null!;
    public string BaselineCommit { get; private set; } = null!;
    public string BaseBranch { get; private set; } = null!;
    public string WorkBranch { get; private set; } = null!;
    public string CredentialKey { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}
