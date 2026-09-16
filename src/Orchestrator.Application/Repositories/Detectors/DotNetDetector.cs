using System.Xml.Linq;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Repositories.Detectors;

/// <summary>Finds .NET projects, NuGet and project-reference dependencies.</summary>
public sealed class DotNetDetector : IRepositoryDetector
{
    public DetectorResult Detect(string root, IReadOnlyList<string> relativePaths)
    {
        var projects = new List<DetectedProject>();
        var dependencies = new List<DetectedDependency>();

        var csprojs = relativePaths
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var hasSolution = relativePaths.Any(f =>
            f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

        foreach (var csproj in csprojs)
        {
            projects.Add(new DetectedProject(csproj, "dotnet"));
            dependencies.AddRange(FindDependencies(root, csproj));
        }

        BuildCommands? commands = csprojs.Count > 0 || hasSolution
            ? new BuildCommands(
                "dotnet restore", "dotnet build --no-restore", "dotnet test --no-build",
                null, ".", TimeSpan.FromMinutes(20))
            : null;

        return new DetectorResult(projects, dependencies, [], [], commands);
    }

    private static IEnumerable<DetectedDependency> FindDependencies(string root, string csproj)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(Path.Combine(root, csproj));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            yield break;
        }

        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName == "PackageReference")
            {
                var include = element.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                    yield return new DetectedDependency(
                        "nuget", include, csproj, element.Attribute("Version")?.Value);
            }
            else if (element.Name.LocalName == "ProjectReference")
            {
                var include = element.Attribute("Include")?.Value;
                if (!string.IsNullOrWhiteSpace(include))
                    yield return new DetectedDependency(
                        "project-reference", include.Replace('\\', '/'), csproj, null);
            }
        }
    }
}
