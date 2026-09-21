namespace Orchestrator.Api.Configuration;

/// <summary>
/// Locates the repository root by walking up from a start directory to the
/// directory containing <c>Orchestrator.sln</c>. Repo-scoped relative paths
/// (the dev vault in <c>.env</c> and <c>appsettings.Development.json</c>)
/// resolve against this so they land in the same place regardless of the
/// process working directory. Falls back to <paramref name="fallback"/>
/// (the API content root) when no solution file is found, e.g. in a
/// published container that ships without the repo.
/// </summary>
public static class RepoRootFinder
{
    private const string SolutionFileName = "Orchestrator.sln";

    public static string Find(string startDirectory, string fallback, int maxLevels = 6)
    {
        var directory = Path.GetFullPath(startDirectory);
        for (var level = 0; level <= maxLevels; level++)
        {
            if (File.Exists(Path.Combine(directory, SolutionFileName)))
                return directory;

            var parent = Directory.GetParent(directory);
            if (parent is null)
                break;

            directory = parent.FullName;
        }

        return Path.GetFullPath(fallback);
    }
}
