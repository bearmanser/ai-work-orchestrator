namespace Orchestrator.Api.Configuration;

/// <summary>
/// Resolves a configured vault (or index) path. Empty stays empty so
/// <c>Program.cs</c> keeps falling back to <c>NullVaultStore</c>; absolute
/// paths are canonicalized; relative paths resolve against the API's content
/// root so a repo-local default (e.g. <c>../../vault</c>) works regardless of
/// the process working directory. <c>Path.Combine</c> already returns a rooted
/// second argument unchanged, so one line covers both cases.
/// </summary>
public static class VaultPathResolver
{
    public static string Resolve(string? configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return string.Empty;

        return Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }
}
