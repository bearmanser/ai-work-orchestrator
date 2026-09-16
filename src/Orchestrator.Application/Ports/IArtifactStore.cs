using Orchestrator.Domain.Evidence;

namespace Orchestrator.Application.Ports;

/// <summary>Describes the content to store. The id is assigned by the store.</summary>
public sealed record ArtifactDescriptor(
    Guid TaskId, string Type, string ContentType, ArtifactSensitivity Sensitivity, string Producer);

/// <summary>
/// Content-addressed storage for large task content (diffs, logs, transcripts,
/// context snapshots). Metadata is persisted in the orchestrator database;
/// bytes live on disk. Content read back is re-hashed and rejected on mismatch.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Stores content and returns the new artifact id.</summary>
    Task<string> PutAsync(ArtifactDescriptor descriptor, Stream content, CancellationToken ct);

    /// <summary>Opens verified content. Throws when the id is unknown or the hash mismatches.</summary>
    Task<Stream> OpenAsync(string artifactId, CancellationToken ct);
}
