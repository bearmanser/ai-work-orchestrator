using System.Security.Cryptography;
using System.Text;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Context;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Context;

/// <summary>Reads repository files for context assembly. Mirrors when present, empty when absent.</summary>
public interface IWorkspaceFileReader
{
    Task<IReadOnlyList<string>> ListFilesAsync(Guid repositoryId, string baseBranch, CancellationToken ct);
    Task<string?> ReadFileAsync(Guid repositoryId, string baseBranch, string path, CancellationToken ct);
}

/// <summary>Durable storage for context snapshots. Implemented in Infrastructure.</summary>
public interface IContextSnapshotStore
{
    Task AddAsync(ContextSnapshot snapshot, CancellationToken ct);
    Task<ContextSnapshot?> GetAsync(Guid snapshotId, CancellationToken ct);
}

/// <summary>
/// Gathers task context in priority order until the budget is spent: (1)
/// instruction files from in-scope profiles, (2) relationship evidence
/// excerpts, (3) files matching the objective's keywords, (4) vault notes.
/// Access filtering happens before retrieval — a repository outside the
/// envelope is never read. Everything dropped is recorded as omitted for
/// budget or for access, and the snapshot preserves source versions.
/// </summary>
public sealed class ContextAssembler(
    IRepositoryProfileStore profiles,
    IRelationshipStore relationships,
    IWorkspaceFileReader files,
    IVaultStore? vault,
    IContextSnapshotStore snapshots,
    IArtifactStore artifacts,
    IPlanningScopeStore scope,
    TimeProvider clock)
{
    public async Task<(EvidenceBundle Bundle, Guid SnapshotId)> AssembleAsync(
        Guid taskId, AuthorizationEnvelope envelope, ContextBudget budget, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var task = await scope.GetTaskAsync(taskId, ct);
        var allProfiles = await profiles.ListAsync(ct);
        var byId = allProfiles.ToDictionary(p => p.Id);
        var allowed = envelope.Repositories.Select(r => r.RepositoryId).ToHashSet();

        var items = new List<EvidenceItem>();
        var omittedBudget = new List<string>();
        var omittedAccess = new List<string>();
        var usedChars = 0;
        var fileCounts = new Dictionary<Guid, int>();

        bool WithinBudget(string identifier, string content, Guid repoId)
        {
            if (usedChars + content.Length > budget.MaxCharacters)
            {
                omittedBudget.Add($"dropped '{identifier}': over character budget");
                return false;
            }

            fileCounts.TryGetValue(repoId, out var count);
            if (count >= budget.MaxFilesPerRepository)
            {
                omittedBudget.Add($"dropped '{identifier}': over per-repository file budget");
                return false;
            }

            return true;
        }

        void Add(string identifier, string version, string content, Guid repoId, EvidenceSource source)
        {
            items.Add(new EvidenceItem(source, identifier, version, content));
            usedChars += content.Length;
            fileCounts[repoId] = fileCounts.TryGetValue(repoId, out var count) ? count + 1 : 1;
        }

        foreach (var repositoryId in allowed)
        {
            if (!byId.TryGetValue(repositoryId, out var profile))
            {
                omittedAccess.Add($"repository '{repositoryId}' is not in the catalog");
                continue;
            }

            var version = profile.LastInspectedCommit ?? "unverified";

            foreach (var path in profile.InstructionFilePaths())
            {
                var content = await files.ReadFileAsync(repositoryId, profile.DefaultBranch, path, ct);
                if (content is null)
                    continue;
                var identifier = $"{profile.Name}:{path}";
                if (WithinBudget(identifier, content, repositoryId))
                    Add(identifier, version, content, repositoryId, EvidenceSource.RepositoryFile);
            }

            foreach (var relationship in await relationships.ListForRepositoryAsync(repositoryId, ct))
            {
                var otherId = relationship.SourceRepositoryId == repositoryId
                    ? relationship.TargetRepositoryId
                    : relationship.SourceRepositoryId;
                if (!allowed.Contains(otherId))
                {
                    var otherName = byId.TryGetValue(otherId, out var other) ? other.Name : otherId.ToString();
                    var note = $"repository '{otherName}' is outside the task's authorized scope";
                    if (!omittedAccess.Contains(note))
                        omittedAccess.Add(note);
                    continue;
                }

                foreach (var evidence in relationship.Evidence)
                {
                    var identifier = $"{profile.Name}:{evidence.SourcePath}";
                    if (WithinBudget(identifier, evidence.Excerpt, repositoryId))
                    {
                        Add(identifier, relationship.LastVerifiedCommit ?? "unverified",
                            evidence.Excerpt, repositoryId, EvidenceSource.RepositoryFile);
                    }
                }
            }

            foreach (var path in await KeywordFilesAsync(repositoryId, profile, task.Objective, ct))
            {
                var content = await files.ReadFileAsync(repositoryId, profile.DefaultBranch, path, ct);
                if (content is null)
                    continue;
                var identifier = $"{profile.Name}:{path}";
                if (WithinBudget(identifier, content, repositoryId))
                    Add(identifier, version, content, repositoryId, EvidenceSource.RepositoryFile);
            }
        }

        if (vault is not null && budget.MaxVaultNotes > 0)
        {
            var notes = await vault.SearchAsync(new VaultQuery(
                task.Objective.Length > 200 ? task.Objective[..200] : task.Objective,
                envelope.VaultPathPrefixes, budget.MaxVaultNotes), ct);
            foreach (var note in notes)
            {
                if (usedChars + note.Body.Length > budget.MaxCharacters)
                {
                    omittedBudget.Add($"dropped vault note '{note.Path}': over character budget");
                    continue;
                }

                items.Add(new EvidenceItem(EvidenceSource.VaultNote, note.Path, note.ContentHash, note.Body));
                usedChars += note.Body.Length;
            }
        }

        var artifactId = await artifacts.PutAsync(
            new ArtifactDescriptor(taskId, "context-snapshot", "text/plain",
                ArtifactSensitivity.Internal, "context-assembler"),
            new MemoryStream(Encoding.UTF8.GetBytes(Render(items, omittedBudget, omittedAccess))), ct);

        var snapshot = new ContextSnapshot(
            taskId,
            artifactId,
            items.Select(i => new ContextSource(
                i.Source.ToString(), i.Identifier, i.Version, Hash(i.Content))).ToList(),
            now,
            $"repos:{allowed.Count};vault:{string.Join(",", envelope.VaultPathPrefixes)}",
            budget.MaxCharacters,
            omittedBudget.Concat(omittedAccess).ToList());
        await snapshots.AddAsync(snapshot, ct);

        return (new EvidenceBundle(items, omittedBudget, omittedAccess), snapshot.Id);
    }

    private async Task<IReadOnlyList<string>> KeywordFilesAsync(
        Guid repositoryId, RepositoryProfile profile, string objective, CancellationToken ct)
    {
        var keywords = objective
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ':', ';', '-', '_', '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length >= 4)
            .Distinct()
            .Take(10)
            .ToList();

        if (keywords.Count == 0)
            return [];

        var matches = new List<(string Path, int Score)>();
        foreach (var path in await files.ListFilesAsync(repositoryId, profile.DefaultBranch, ct))
        {
            var lower = path.ToLowerInvariant();
            var score = keywords.Count(k => lower.Contains(k, StringComparison.Ordinal));
            if (score > 0)
                matches.Add((path, score));
        }

        return matches.OrderByDescending(m => m.Score).Select(m => m.Path).ToList();
    }

    private static string Render(
        IReadOnlyList<EvidenceItem> items,
        IReadOnlyList<string> omittedBudget,
        IReadOnlyList<string> omittedAccess)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
            sb.AppendLine($"# [{item.Source}] {item.Identifier} ({item.Version})").AppendLine(item.Content);
        foreach (var omission in omittedBudget)
            sb.AppendLine($"# omitted for budget: {omission}");
        foreach (var omission in omittedAccess)
            sb.AppendLine($"# omitted for access: {omission}");
        return sb.ToString();
    }

    private static string Hash(string content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
