namespace Orchestrator.Adapters.Git;

/// <summary>
/// On-disk layout under the adapter's git root. Mirrors are per-repository bare
/// clones fetched once; workspaces are per-task checkouts cloned from the local
/// mirror, so N tasks over one repository cost one network fetch.
/// </summary>
public static class WorkspaceLayout
{
    public static string MirrorPath(string gitRoot, Guid repositoryId) =>
        Path.Combine(gitRoot, "mirrors", repositoryId.ToString("N") + ".git");

    public static string WorkspacePath(string gitRoot, Guid taskId, Guid repositoryId) =>
        Path.Combine(gitRoot, "workspaces", taskId.ToString("N"), repositoryId.ToString("N"));
}
