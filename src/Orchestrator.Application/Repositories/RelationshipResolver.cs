using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Repositories;

/// <summary>What goes to a person instead of being guessed.</summary>
public sealed record OpenQuestion(string Subject, string Detail, Guid? RelatedRepositoryId);

public sealed record ResolutionOutcome(
    IReadOnlyList<RepositoryRelationship> Relationships,
    IReadOnlyList<OpenQuestion> Questions);

/// <summary>
/// Derives repository relationships from source evidence only. A relationship
/// exists when a file proves it; an unmatched reference becomes an
/// <see cref="OpenQuestion"/>; an admin assertion contradicted by fresh
/// evidence is marked <c>Conflicted</c> but never deleted.
/// </summary>
public sealed class RelationshipResolver(TimeProvider clock)
{
    private const string UnverifiedCommit = "unverified";

    public Task<ResolutionOutcome> ResolveAsync(
        IReadOnlyList<(RepositoryProfile Profile, InspectionResult Inspection)> inspected,
        IReadOnlyList<RepositoryRelationship>? existingRelationships = null,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var existing = existingRelationships ?? [];
        var found = new Dictionary<(Guid Source, Guid Target, RelationshipType Type), RepositoryRelationship>();
        var questions = new List<OpenQuestion>();

        foreach (var (profile, inspection) in inspected)
        {
            foreach (var dependency in inspection.Dependencies)
            {
                ct.ThrowIfCancellationRequested();
                MatchDependency(profile, dependency, inspected, found, questions, now);
            }
        }

        var relationships = existing.Concat(found.Values).ToList();
        foreach (var asserted in existing.Where(r => r.Status == RelationshipStatus.AdminAsserted))
        {
            var source = inspected.FirstOrDefault(i => i.Profile.Id == asserted.SourceRepositoryId);
            if (source.Profile is null || IsEmpty(source.Inspection))
                continue;
            if (!Supports(asserted, found.Values))
                asserted.MarkConflicted();
        }

        return Task.FromResult(new ResolutionOutcome(relationships, questions));
    }

    private static void MatchDependency(
        RepositoryProfile source,
        DetectedDependency dependency,
        IReadOnlyList<(RepositoryProfile Profile, InspectionResult Inspection)> inspected,
        Dictionary<(Guid, Guid, RelationshipType), RepositoryRelationship> found,
        List<OpenQuestion> questions,
        DateTimeOffset now)
    {
        if (dependency.Kind == "http-base-url" || dependency.Kind == "generated-client")
        {
            var host = HostOf(dependency.Identifier);
            var target = host is null ? null : inspected
                .Select(i => i.Profile)
                .FirstOrDefault(p => p.Id != source.Id
                    && inspected.First(i => i.Profile.Id == p.Id).Inspection.ApiSurfaces
                        .Any(s => HostOf(s.Identifier) == host));
            var type = dependency.Kind == "http-base-url"
                ? RelationshipType.ConsumesHttpApi
                : RelationshipType.GeneratesClientFrom;

            if (target is null)
            {
                if (dependency.Kind == "http-base-url")
                {
                    questions.Add(new OpenQuestion(
                        $"Unresolved service reference in {source.Name}",
                        $"No configured repository serves '{dependency.Identifier}' " +
                        $"referenced by '{dependency.SourcePath}'.",
                        source.Id));
                }
            }
            else
            {
                AddEvidence(found, source, target, type,
                    new RelationshipEvidence(
                        dependency.SourcePath,
                        source.LastInspectedCommit ?? UnverifiedCommit,
                        dependency.Identifier),
                    now);
            }

            return;
        }

        if (dependency.Kind is "nuget" or "npm")
        {
            var target = inspected
                .Select(i => i.Profile)
                .FirstOrDefault(p => p.Id != source.Id
                    && inspected.First(i => i.Profile.Id == p.Id).Inspection.Projects
                        .Any(pr => dependency.Identifier.Equals(pr.Identifier, StringComparison.OrdinalIgnoreCase)));
            if (target is not null)
            {
                AddEvidence(found, source, target, RelationshipType.SharesLibrary,
                    new RelationshipEvidence(
                        dependency.SourcePath,
                        source.LastInspectedCommit ?? UnverifiedCommit,
                        $"{dependency.Identifier} {dependency.Version}".Trim()),
                    now);
            }
        }
    }

    private static void AddEvidence(
        Dictionary<(Guid, Guid, RelationshipType), RepositoryRelationship> found,
        RepositoryProfile source, RepositoryProfile target, RelationshipType type,
        RelationshipEvidence evidence, DateTimeOffset now)
    {
        if (!found.TryGetValue((source.Id, target.Id, type), out var relationship))
        {
            relationship = new RepositoryRelationship(
                source.Id, target.Id, type, [], now,
                source.LastInspectedCommit);
            found[(source.Id, target.Id, type)] = relationship;
        }

        relationship.AddEvidence(evidence);
    }

    private static bool Supports(
        RepositoryRelationship asserted, IEnumerable<RepositoryRelationship> fresh) =>
        fresh.Any(r => r.SourceRepositoryId == asserted.SourceRepositoryId
            && r.TargetRepositoryId == asserted.TargetRepositoryId
            && r.RelationshipType == asserted.RelationshipType);

    private static bool IsEmpty(InspectionResult inspection) =>
        inspection.Projects.Count == 0
        && inspection.Dependencies.Count == 0
        && inspection.InstructionFiles.Count == 0
        && inspection.ApiSurfaces.Count == 0;

    private static string? HostOf(string? value)
    {
        if (value is null)
            return null;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.Host.ToLowerInvariant()
            : null;
    }
}
