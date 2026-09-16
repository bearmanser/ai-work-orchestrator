using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Repositories;

public sealed record DetectedProject(string Path, string Ecosystem);
public sealed record DetectedApiSurface(string Path, string Kind);
public sealed record DetectedDependency(string Kind, string Identifier, string SourcePath, string? Version);

/// <summary>One detector's partial findings; the inspector merges them.</summary>
public sealed record DetectorResult(
    IReadOnlyList<DetectedProject> Projects,
    IReadOnlyList<DetectedDependency> Dependencies,
    IReadOnlyList<string> InstructionFiles,
    IReadOnlyList<DetectedApiSurface> ApiSurfaces,
    BuildCommands? SuggestedCommands);

public sealed record InspectionResult(
    IReadOnlyList<DetectedProject> Projects,
    BuildCommands? SuggestedCommands,
    IReadOnlyList<string> InstructionFiles,
    IReadOnlyList<DetectedApiSurface> ApiSurfaces,
    IReadOnlyList<DetectedDependency> Dependencies);

/// <summary>
/// Inspects one ecosystem's files. A new ecosystem is one class, not a change
/// to the inspector. All reported paths are relative with `/` separators.
/// </summary>
public interface IRepositoryDetector
{
    DetectorResult Detect(string root, IReadOnlyList<string> relativePaths);
}
