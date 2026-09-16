using System.Text.Json;

namespace Orchestrator.Domain.Repositories;

public enum RelationshipType
{
    ConsumesHttpApi,
    SharesLibrary,
    GeneratesClientFrom,
    DependsOnPackage,
}

public enum RelationshipStatus
{
    Evidenced,
    AdminAsserted,
    Conflicted,
}

/// <summary>
/// How two repositories work together. Exists only when a file proves it —
/// except <see cref="RelationshipStatus.AdminAsserted"/>, which is a human's
/// explicit correction and is never deleted or silently overwritten by
/// automation.
/// </summary>
public sealed class RepositoryRelationship
{
    private RepositoryRelationship() { }

    public RepositoryRelationship(
        Guid sourceRepositoryId, Guid targetRepositoryId, RelationshipType relationshipType,
        IReadOnlyList<RelationshipEvidence> evidence, DateTimeOffset verifiedAt,
        string? verifiedCommit, string? adminNote = null,
        RelationshipStatus status = RelationshipStatus.Evidenced)
    {
        Id = Guid.CreateVersion7();
        SourceRepositoryId = sourceRepositoryId;
        TargetRepositoryId = targetRepositoryId;
        RelationshipType = relationshipType;
        EvidenceJson = JsonSerializer.Serialize(evidence);
        Status = status;
        LastVerifiedAt = verifiedAt.ToUniversalTime();
        LastVerifiedCommit = verifiedCommit;
        AdminNote = adminNote;
    }

    public static RepositoryRelationship AssertByAdmin(
        Guid sourceRepositoryId, Guid targetRepositoryId, RelationshipType relationshipType,
        string note, DateTimeOffset now) => new(
        sourceRepositoryId, targetRepositoryId, relationshipType, [],
        now, null, note, RelationshipStatus.AdminAsserted);

    public Guid Id { get; private set; }
    public Guid SourceRepositoryId { get; private set; }
    public Guid TargetRepositoryId { get; private set; }
    public RelationshipType RelationshipType { get; private set; }
    public string EvidenceJson { get; private set; } = "[]";
    public RelationshipStatus Status { get; private set; }
    public DateTimeOffset LastVerifiedAt { get; private set; }
    public string? LastVerifiedCommit { get; private set; }
    public string? AdminNote { get; private set; }

    public IReadOnlyList<RelationshipEvidence> Evidence =>
        JsonSerializer.Deserialize<List<RelationshipEvidence>>(EvidenceJson) ?? [];

    public void AddEvidence(RelationshipEvidence evidence)
    {
        var list = Evidence.ToList();
        list.Add(evidence);
        EvidenceJson = JsonSerializer.Serialize(list);
    }

    public void MarkConflicted() => Status = RelationshipStatus.Conflicted;
}
