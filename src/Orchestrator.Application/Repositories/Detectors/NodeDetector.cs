using System.Text.Json;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Repositories.Detectors;

/// <summary>Finds Node.js packages, npm dependencies, and script commands.</summary>
public sealed class NodeDetector : IRepositoryDetector
{
    public DetectorResult Detect(string root, IReadOnlyList<string> relativePaths)
    {
        var projects = new List<DetectedProject>();
        var dependencies = new List<DetectedDependency>();
        BuildCommands? commands = null;

        foreach (var manifest in relativePaths.Where(f =>
            f.Split('/').Last().Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            JsonElement package;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, manifest)));
                package = document.RootElement.Clone();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            projects.Add(new DetectedProject(manifest, "node", PackageName(package)));
            dependencies.AddRange(ReadDependencies(package, "dependencies", manifest));
            dependencies.AddRange(ReadDependencies(package, "devDependencies", manifest));

            commands ??= SuggestCommands(root, manifest, package);
        }

        return new DetectorResult(projects, dependencies, [], [], commands);
    }

    private static string? PackageName(JsonElement package) =>
        package.TryGetProperty("name", out var name)
        && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

    private static IEnumerable<DetectedDependency> ReadDependencies(
        JsonElement package, string section, string sourcePath)
    {
        if (!package.TryGetProperty(section, out var entries)
            || entries.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var entry in entries.EnumerateObject())
        {
            yield return new DetectedDependency(
                "npm", entry.Name, sourcePath,
                entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null);
        }
    }

    private static BuildCommands SuggestCommands(string root, string manifest, JsonElement package)
    {
        var scripts = package.TryGetProperty("scripts", out var s)
            && s.ValueKind == JsonValueKind.Object
            ? s.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var directory = Path.GetDirectoryName(manifest.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var hasLockFile = File.Exists(Path.Combine(root, directory, "package-lock.json"));

        string? Script(string name) => scripts.Contains(name) ? $"npm run {name}" : null;

        return new BuildCommands(
            hasLockFile ? "npm ci" : "npm install",
            Script("build"), Script("test"), Script("lint"),
            ".", TimeSpan.FromMinutes(10));
    }
}
