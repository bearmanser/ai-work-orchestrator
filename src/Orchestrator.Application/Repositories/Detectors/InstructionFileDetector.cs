namespace Orchestrator.Application.Repositories.Detectors;

/// <summary>Finds agent instruction files (CLAUDE.md, AGENTS.md, CONTRIBUTING.md).</summary>
public sealed class InstructionFileDetector : IRepositoryDetector
{
    private static readonly string[] InstructionNames =
        ["CLAUDE.md", "AGENTS.md", "CONTRIBUTING.md"];

    public DetectorResult Detect(string root, IReadOnlyList<string> relativePaths)
    {
        var files = relativePaths
            .Where(f => InstructionNames.Contains(
                f.Split('/').Last(), StringComparer.OrdinalIgnoreCase))
            .ToList();

        return new DetectorResult([], [], files, [], null);
    }
}
