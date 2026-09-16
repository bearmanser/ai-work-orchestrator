using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Orchestrator.Adapters.Git;

/// <summary>
/// A bare mirror of one remote repository. Created on first use, then fetched
/// with prune before every baseline read so drift checks see the live tip.
/// </summary>
public sealed class RepositoryMirror(string mirrorPath)
{
    public string MirrorPath { get; } = mirrorPath;

    /// <summary>The remote URL recorded in the mirror's own config.</summary>
    public string RemoteUrl
    {
        get
        {
            using var repo = new Repository(MirrorPath);
            return repo.Network.Remotes["origin"]?.Url
                ?? throw new InvalidOperationException($"Mirror '{MirrorPath}' has no origin remote.");
        }
    }

    public void EnsureRemote(string remoteUrl)
    {
        if (!Repository.IsValid(MirrorPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MirrorPath)!);
            Repository.Init(MirrorPath, isBare: true);
        }

        using var repo = new Repository(MirrorPath);
        var origin = repo.Network.Remotes["origin"];
        if (origin is null)
        {
            repo.Network.Remotes.Add("origin", remoteUrl);
        }
        else if (!origin.Url.Equals(remoteUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Mirror '{MirrorPath}' points at '{origin.Url}', not '{remoteUrl}'.");
        }
    }

    /// <summary>Fetches origin and returns the current tip of the base branch.</summary>
    public string FetchBaseTip(string baseBranch, CredentialsHandler? credentials)
    {
        using var repo = new Repository(MirrorPath);
        Commands.Fetch(repo, "origin", [], new FetchOptions
        {
            CredentialsProvider = credentials,
            Prune = true,
            TagFetchMode = TagFetchMode.None,
        }, null);

        return repo.Branches[$"origin/{baseBranch}"]?.Tip?.Sha
            ?? throw new InvalidOperationException(
                $"Base branch '{baseBranch}' was not found on the remote.");
    }
}
