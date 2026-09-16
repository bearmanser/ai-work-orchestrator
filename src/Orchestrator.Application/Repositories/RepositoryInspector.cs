using System.IO;
using System.Text.Json;

namespace Orchestrator.Application.Repositories;

/// <summary>
/// Walks a checkout once and merges per-ecosystem detector findings with
/// ecosystem-agnostic content scans (OpenAPI surfaces, configured base URLs,
/// generated-client configs). Every finding carries its source path; nothing
/// is ever inferred from a directory name alone.
/// </summary>
public sealed class RepositoryInspector(params IRepositoryDetector[] detectors)
{
    private static readonly string[] SkippedDirectories =
        ["node_modules", "bin", "obj", "dist", ".git", ".vs"];

    public async Task<InspectionResult> InspectAsync(string workspacePath, CancellationToken ct)
    {
        var root = Path.GetFullPath(workspacePath);
        var files = await Task.Run(() => CollectFiles(root), ct);
        var partials = detectors.Select(d => d.Detect(root, files)).ToArray();
        var core = DetectCore(root, files);

        return new InspectionResult(
            partials.SelectMany(p => p.Projects).Concat(core.Projects).ToList(),
            partials.Select(p => p.SuggestedCommands).FirstOrDefault(c => c is not null),
            partials.SelectMany(p => p.InstructionFiles).Concat(core.InstructionFiles).ToList(),
            partials.SelectMany(p => p.ApiSurfaces).Concat(core.ApiSurfaces).ToList(),
            partials.SelectMany(p => p.Dependencies).Concat(core.Dependencies).ToList());
    }

    private static IReadOnlyList<string> CollectFiles(string root)
    {
        var patterns = ReadGitignore(root);
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(p => !p.Split('/').Any(s => SkippedDirectories.Contains(s, StringComparer.Ordinal)))
            .Where(p => !IsIgnored(p, patterns))
            .ToList();
    }

    private static IReadOnlyList<string> ReadGitignore(string root)
    {
        var path = Path.Combine(root, ".gitignore");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    internal static bool IsIgnored(string relativePath, IReadOnlyList<string> patterns)
    {
        var segments = relativePath.Split('/');
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#') || pattern.StartsWith('!'))
                continue;
            pattern = pattern.TrimStart('/');

            if (pattern.EndsWith('/'))
            {
                if (segments.Contains(pattern.TrimEnd('/'), StringComparer.Ordinal))
                    return true;
            }
            else if (pattern.Contains('/'))
            {
                if (relativePath.Equals(pattern, StringComparison.Ordinal))
                    return true;
            }
            else if (segments.Any(s => GlobMatches(s, pattern)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Minimal <c>*</c>/<c>?</c> glob matcher for gitignore file patterns.</summary>
    internal static bool GlobMatches(string name, string pattern)
    {
        var n = 0;
        var p = 0;
        var star = -1;
        var mark = 0;

        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star != -1)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    private static DetectorResult DetectCore(string root, IReadOnlyList<string> files)
    {
        var surfaces = new List<DetectedApiSurface>();
        var dependencies = new List<DetectedDependency>();

        foreach (var file in files)
        {
            var name = file.Split('/').Last().ToLowerInvariant();
            if (IsOpenApiFile(name))
                surfaces.Add(new DetectedApiSurface(file, "openapi"));
            if (name.EndsWith(".nswag", StringComparison.Ordinal))
                dependencies.Add(new DetectedDependency(
                    "generated-client", NswagUrl(root, file) ?? file, file, null));
            if (name.EndsWith(".json", StringComparison.Ordinal))
                dependencies.AddRange(FindBaseUrls(root, file));
        }

        return new DetectorResult([], dependencies, [], surfaces, null);
    }

    /// <summary>The first absolute http(s) URL inside an NSwag config, if any.</summary>
    private static string? NswagUrl(string root, string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, file)));
            return WalkStrings(document.RootElement)
                .Select(v => v.Value)
                .FirstOrDefault(v => v is not null
                    && Uri.TryCreate(v, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsOpenApiFile(string fileName) =>
        (fileName.Contains("openapi", StringComparison.Ordinal)
            || fileName.StartsWith("swagger", StringComparison.Ordinal))
        && (fileName.EndsWith(".json", StringComparison.Ordinal)
            || fileName.EndsWith(".yaml", StringComparison.Ordinal)
            || fileName.EndsWith(".yml", StringComparison.Ordinal));

    private static IEnumerable<DetectedDependency> FindBaseUrls(string root, string file)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, file)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var (name, value) in WalkStrings(document.RootElement))
            {
                if (!IsUrlProperty(name) || value is null)
                    continue;
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    yield return new DetectedDependency("http-base-url", value, file, null);
            }
        }
    }

    private static bool IsUrlProperty(string name) =>
        name.EndsWith("url", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("uri", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("endpoint", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("endpoints", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Name, string? Value)> WalkStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                        yield return (property.Name, property.Value.GetString());
                    else
                    {
                        foreach (var nested in WalkStrings(property.Value))
                            yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in WalkStrings(item))
                        yield return nested;
                }

                break;
        }
    }
}
