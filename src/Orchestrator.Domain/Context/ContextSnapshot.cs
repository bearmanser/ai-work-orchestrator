using System.Text.Json;

namespace Orchestrator.Domain.Context;

/// <summary>One evidence source that contributed to a context snapshot.</summary>
public sealed record ContextSource(string Kind, string Identifier, string Version, string ContentHash);

/// <summary>
/// Records what evidence an agent was shown (and what was omitted) at one point
/// in time. The assembled content itself lives in the artifact store as an
/// artifact of type <c>context-snapshot</c>; this row carries the provenance.
/// </summary>
public sealed class ContextSnapshot
{
    private ContextSnapshot() { }

    public ContextSnapshot(Guid taskId, string artifactId, IReadOnlyList<ContextSource> sources,
                           DateTimeOffset retrievedAt, string accessScope, int tokenBudget,
                           IReadOnlyList<string> omittedSources)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
            throw new ArgumentException("Artifact id is required.", nameof(artifactId));
        if (string.IsNullOrWhiteSpace(accessScope))
            throw new ArgumentException("Access scope is required.", nameof(accessScope));
        ArgumentOutOfRangeException.ThrowIfNegative(tokenBudget);

        Id = Guid.CreateVersion7();
        TaskId = taskId;
        ArtifactId = artifactId;
        SourcesJson = JsonSerializer.Serialize(sources);
        RetrievedAt = retrievedAt.ToUniversalTime();
        AccessScope = accessScope;
        TokenBudget = tokenBudget;
        OmittedSourcesJson = JsonSerializer.Serialize(omittedSources);
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public string ArtifactId { get; private set; } = null!;
    public string SourcesJson { get; private set; } = null!;
    public DateTimeOffset RetrievedAt { get; private set; }
    public string AccessScope { get; private set; } = null!;
    public int TokenBudget { get; private set; }
    public string OmittedSourcesJson { get; private set; } = null!;
}
