using DotNetEnv;

namespace Orchestrator.Api.Configuration;

/// <summary>
/// Loads a repo-root <c>.env</c> file into the process environment before the
/// host builds its configuration, so local development can keep secrets out of
/// the shell profile. Real environment variables always win; a missing file is
/// silently ignored so container deployments are unaffected.
/// </summary>
public static class DotEnvLoader
{
    public static string? FindDotEnvFile(string startDirectory, int maxLevels = 6)
    {
        var directory = Path.GetFullPath(startDirectory);
        for (var level = 0; level <= maxLevels; level++)
        {
            var candidate = Path.Combine(directory, ".env");
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(directory);
            if (parent is null)
                return null;

            directory = parent.FullName;
        }

        return null;
    }

    public static void Load(string path)
    {
        // NoClobber: values already present in the process environment win.
        Env.Load(path, Env.NoClobber());
    }

    public static void LoadFromRepoRoot()
    {
        var path = FindDotEnvFile(AppContext.BaseDirectory);
        if (path is not null)
            Load(path);
    }
}
