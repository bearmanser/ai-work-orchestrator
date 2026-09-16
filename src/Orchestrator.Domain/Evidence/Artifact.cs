namespace Orchestrator.Domain.Evidence;

/// <summary>How widely an artifact's content may be shared.</summary>
public enum ArtifactSensitivity
{
    Internal,
    Restricted,
}

/// <summary>
/// Metadata for one large content blob (diff, test log, transcript, context
/// snapshot). The bytes live in the artifact store; this row is the durable,
/// content-addressed pointer to them.
/// </summary>
public sealed class Artifact
{
    private Artifact() { }

    public Artifact(Guid taskId, string type, string contentType, ArtifactSensitivity sensitivity,
                    string producer, string contentHash, long sizeBytes,
                    DateTimeOffset createdAt, DateTimeOffset retainUntil)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Type is required.", nameof(type));
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("Content type is required.", nameof(contentType));
        if (string.IsNullOrWhiteSpace(producer))
            throw new ArgumentException("Producer is required.", nameof(producer));
        if (string.IsNullOrWhiteSpace(contentHash))
            throw new ArgumentException("Content hash is required.", nameof(contentHash));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);

        Id = Guid.CreateVersion7().ToString("N");
        TaskId = taskId;
        Type = type;
        ContentType = contentType;
        Sensitivity = sensitivity;
        Producer = producer;
        ContentHash = contentHash;
        SizeBytes = sizeBytes;
        CreatedAt = createdAt.ToUniversalTime();
        RetainUntil = retainUntil.ToUniversalTime();
    }

    public string Id { get; private set; } = null!;
    public Guid TaskId { get; private set; }
    public string Type { get; private set; } = null!;
    public string ContentType { get; private set; } = null!;
    public ArtifactSensitivity Sensitivity { get; private set; }
    public string Producer { get; private set; } = null!;
    public string ContentHash { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset RetainUntil { get; private set; }
}

/// <summary>Thrown when stored bytes no longer match the recorded content hash.</summary>
public sealed class ArtifactIntegrityException(string artifactId)
    : Exception($"Artifact '{artifactId}' failed integrity verification.")
{
    public string ArtifactId { get; } = artifactId;
}

/// <summary>Thrown when no artifact exists for the requested id.</summary>
public sealed class ArtifactNotFoundException(string artifactId)
    : Exception($"Artifact '{artifactId}' was not found.")
{
    public string ArtifactId { get; } = artifactId;
}
