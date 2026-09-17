using LibGit2Sharp;
using Orchestrator.Application.Context;

namespace Orchestrator.Adapters.Git;

/// <summary>
/// Reads files straight from a repository's bare mirror, with no checkout.
/// Returns empty when the mirror has never been fetched: planning degrades to
/// profiles, relationships, and vault notes rather than failing. (Workspaces
/// with work branches arrive in Task 38, which also populates these mirrors.)
/// </summary>
public sealed class MirrorFileReader(GitOptions options) : IWorkspaceFileReader
{
    public Task<IReadOnlyList<string>> ListFilesAsync(
        Guid repositoryId, string baseBranch, CancellationToken ct)
    {
        var mirrorPath = MirrorPath(repositoryId);
        if (!Repository.IsValid(mirrorPath))
            return Task.FromResult<IReadOnlyList<string>>([]);

        return Task.Run(() =>
        {
            using var repo = new Repository(mirrorPath);
            var tip = repo.Branches[$"origin/{baseBranch}"]?.Tip;
            if (tip is null)
                return (IReadOnlyList<string>)[];
            return tip.Tree
                .SelectMany(Flatten)
                .ToList();
        }, ct);
    }

    public Task<string?> ReadFileAsync(
        Guid repositoryId, string baseBranch, string path, CancellationToken ct)
    {
        var mirrorPath = MirrorPath(repositoryId);
        if (!Repository.IsValid(mirrorPath))
            return Task.FromResult<string?>(null);

        return Task.Run(() =>
        {
            using var repo = new Repository(mirrorPath);
            var tip = repo.Branches[$"origin/{baseBranch}"]?.Tip;
            var entry = tip?[path.Replace('\\', '/')];
            if (entry?.Target is not Blob blob)
                return null;

            using var content = blob.GetContentStream();
            using var reader = new StreamReader(content);
            var text = reader.ReadToEnd();
            // Binary blobs are not usable as prompt context.
            return text.Contains('\0') ? null : text;
        }, ct);
    }

    private string MirrorPath(Guid repositoryId) =>
        WorkspaceLayout.MirrorPath(options.Root, repositoryId);

    // LibGit2Sharp entry paths are already root-relative, so no prefixing is needed.
    private static IEnumerable<string> Flatten(TreeEntry entry) =>
        entry.TargetType == TreeEntryTargetType.Tree
            ? ((Tree)entry.Target).SelectMany(Flatten)
            : [entry.Path];
}
