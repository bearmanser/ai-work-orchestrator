using System.Text.Json;
using Orchestrator.Application.Agents;
using Orchestrator.Application.Evidence;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Memory;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Memory;

/// <summary>Durable storage for memory proposals. Implemented in Infrastructure.</summary>
public interface IMemoryProposalStore
{
    Task AddAsync(MemoryProposal proposal, CancellationToken ct);
    Task<MemoryProposal?> GetAsync(Guid id, CancellationToken ct);
}

public sealed record CurationResult(
    IReadOnlyList<MemoryProposal> Published,
    IReadOnlyList<MemoryProposal> Deferred);

/// <summary>Automatic memory curation: propose, validate, and publish.</summary>
public interface IMemoryCurator
{
    Task<CurationResult> CurateAsync(Guid taskId, CancellationToken ct);
    Task RevertAsync(Guid proposalId, CancellationToken ct);
}

/// <summary>Configuration for memory curation.</summary>
public sealed class MemoryCuratorOptions
{
    public string FallbackChannelId { get; set; } = "C-tasks";
}

/// <summary>
/// Publishes durable lessons to the vault without human approval, inside the
/// vault scope the task's envelope allows. Duplicates update the existing note
/// (compare-and-swap, retried once); contradictions are deferred with a Slack
/// notification instead of overwriting; paths outside the envelope are refused.
/// </summary>
public sealed class MemoryCurator(
    AgentRouter router,
    IVaultStore vault,
    IMemoryProposalStore proposals,
    IPlanningScopeStore scope,
    EvidenceAssembler assembler,
    IChatGateway chat,
    IAuditWriter audit,
    TimeProvider clock,
    MemoryCuratorOptions options) : IMemoryCurator
{
    private static readonly string[][] AntonymPairs =
    [
        ["read-only", "read-write"],
        ["enabled", "disabled"],
        ["allowed", "denied"],
        ["required", "optional"],
        ["supported", "unsupported"],
    ];

    public async Task<CurationResult> CurateAsync(Guid taskId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var task = await scope.GetTaskAsync(taskId, ct);
        var envelope = await scope.GetEnvelopeAsync(taskId, ct)
            ?? throw new InvalidOperationException($"Task {taskId} has no authorization envelope.");
        var evidence = await assembler.AssembleAsync(taskId, ct);
        var request = new AgentRequest(
            taskId,
            Guid.CreateVersion7().ToString("N"),
            Ports.AgentStage.Review,
            new TrustedInstructions(
                $"Propose durable lessons from task '{task.Objective}'. Output a single ```json fenced object.",
                ["Each candidate has content, evidence, and an optional target path"],
                ["Do not invent facts without supporting evidence"],
                ["Raw logs", "Secrets", "Transcripts"]),
            ToBundle(evidence),
            new WorkspaceHandle(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
            new ProviderPolicy(envelope.AllowedProviders, FallbackAllowed: false),
            envelope.Budget,
            envelope.Id.ToString());

        var result = await router.ExecuteAsync(request, ct);
        if (result.Status != AgentStatus.Succeeded || string.IsNullOrWhiteSpace(result.OutputText))
            return new CurationResult([], []);

        var published = new List<MemoryProposal>();
        var deferred = new List<MemoryProposal>();
        foreach (var candidate in ParseCandidates(result.OutputText))
        {
            if (!DurabilityRules.IsDurable(candidate).Durable)
                continue;

            var path = ResolvePath(candidate, envelope);
            if (path is null)
                continue;

            var outcome = await PublishAsync(taskId, path, candidate, envelope, now, ct);
            if (outcome.Published is not null)
                published.Add(outcome.Published);
            else if (outcome.Deferred is not null)
                deferred.Add(outcome.Deferred);
        }

        return new CurationResult(published, deferred);
    }

    public async Task RevertAsync(Guid proposalId, CancellationToken ct)
    {
        var proposal = await proposals.GetAsync(proposalId, ct)
            ?? throw new KeyNotFoundException($"Memory proposal '{proposalId}' was not found.");
        if (proposal.VaultPath is null || proposal.VaultRevisionId is null)
            throw new InvalidOperationException($"Memory proposal '{proposalId}' was never published.");

        await vault.RevertAsync(proposal.VaultPath, proposal.VaultRevisionId, ct);
        await audit.RecordAsync(new AuditEntry(
            Actor: "orchestrator",
            TaskId: proposal.SourceTaskId,
            Action: "memory.reverted",
            Reason: $"Reverted vault note '{proposal.VaultPath}'.",
            Outcome: "Success",
            CorrelationId: proposal.Id.ToString(),
            Details: new Dictionary<string, string> { ["path"] = proposal.VaultPath }), ct);
    }

    private static EvidenceBundle ToBundle(TaskEvidence evidence)
    {
        var items = new List<EvidenceItem>();
        items.AddRange(evidence.RuntimeChecks.Select(c =>
            new EvidenceItem(EvidenceSource.TaskEvidence, $"check:{c.Command}", c.LogArtifactId,
                $"{c.Command}: {(c.Passed ? "passed" : "failed")}")));
        items.AddRange(evidence.DomainChecks.Select(c =>
            new EvidenceItem(EvidenceSource.TaskEvidence, $"p1:{c.QueryName}", c.QueryName,
                $"{c.QueryName} on {c.Environment}/{c.Company}: {(c.Passed ? "passed" : "failed")}")));
        items.AddRange(evidence.Diff.ChangedFiles.Select(f =>
            new EvidenceItem(EvidenceSource.TaskEvidence, f, evidence.Diff.HeadCommit, "changed")));
        items.AddRange(evidence.UnresolvedQuestions.Select(q =>
            new EvidenceItem(EvidenceSource.TaskEvidence, "open-question", string.Empty, q)));
        return new EvidenceBundle(items, [], []);
    }

    private async Task<(MemoryProposal? Published, MemoryProposal? Deferred)> PublishAsync(
        Guid taskId, string path, MemoryCandidate candidate,
        AuthorizationEnvelope envelope, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await FindDuplicateAsync(path, candidate, envelope, ct);
        if (existing is not null && Contradicts(existing.Body, candidate.Content))
        {
            var deferred = new MemoryProposal(
                Guid.CreateVersion7(), taskId, candidate.Content,
                candidate.Evidence.Select(e => e.Identifier).ToList(), now,
                SourceTaskId: taskId, VaultPath: existing.Path);
            await proposals.AddAsync(deferred, ct);
            await chat.PostAsync(new ChatMessage(
                envelope.SlackChannels.FirstOrDefault() ?? options.FallbackChannelId,
                null, null,
                $"Memory curation conflict for '{existing.Path}': the new lesson contradicts the existing note. " +
                "Review and resolve it manually."), ct);
            await audit.RecordAsync(new AuditEntry(
                Actor: "orchestrator", TaskId: taskId, Action: "memory.deferred",
                Reason: $"Publication to '{existing.Path}' deferred: contradicts existing note.",
                Outcome: "Deferred", CorrelationId: taskId.ToString(),
                Details: new Dictionary<string, string> { ["path"] = existing.Path }), ct);
            return (null, deferred);
        }

        // An update lands on the existing note's path, never on the fresh slug:
        // otherwise every curation would fork a duplicate instead of refining.
        var targetPath = existing?.Path ?? path;
        NoteFrontmatter frontmatter;
        string? expectedHash;
        if (existing is not null)
        {
            frontmatter = existing.Frontmatter;
            expectedHash = existing.ContentHash;
        }
        else
        {
            frontmatter = DefaultFrontmatter(candidate.Content, now);
            expectedHash = null;
        }

        var write = new VaultWrite(targetPath, frontmatter, candidate.Content, expectedHash);
        if (await vault.WriteAsync(write, ct) == VaultWriteResult.Conflict)
        {
            string? freshHash = null;
            try
            {
                freshHash = (await vault.ReadAsync(targetPath, ct)).ContentHash;
            }
            catch (FileNotFoundException)
            {
                // The note never landed; retry as an unconditional write.
            }

            write = write with { ExpectedContentHash = freshHash };
            if (await vault.WriteAsync(write, ct) == VaultWriteResult.Conflict)
            {
                var deferred = new MemoryProposal(
                    Guid.CreateVersion7(), taskId, candidate.Content,
                    candidate.Evidence.Select(e => e.Identifier).ToList(), now,
                    SourceTaskId: taskId, VaultPath: targetPath);
                await proposals.AddAsync(deferred, ct);
                return (null, deferred);
            }
        }

        var revisions = await vault.GetRevisionsAsync(targetPath, ct);
        var published = new MemoryProposal(
            Guid.CreateVersion7(), taskId, candidate.Content,
            candidate.Evidence.Select(e => e.Identifier).ToList(), now,
            SourceTaskId: taskId, VaultPath: targetPath,
            VaultRevisionId: revisions.LastOrDefault()?.RevisionId);
        await proposals.AddAsync(published, ct);
        await audit.RecordAsync(new AuditEntry(
            Actor: "orchestrator", TaskId: taskId, Action: "memory.published",
            Reason: $"Published durable lesson to '{targetPath}'.",
            Outcome: "Success", CorrelationId: taskId.ToString(),
            Details: new Dictionary<string, string> { ["path"] = targetPath }), ct);
        return (published, null);
    }

    private async Task<VaultNote?> FindDuplicateAsync(
        string path, MemoryCandidate candidate,
        AuthorizationEnvelope envelope, CancellationToken ct)
    {
        VaultNote? direct = null;
        try
        {
            direct = await vault.ReadAsync(path, ct);
            return direct;
        }
        catch (FileNotFoundException)
        {
        }

        var notes = await vault.SearchAsync(new VaultQuery(
            FirstWords(candidate.Content, 8), envelope.VaultPathPrefixes, MaxResults: 5), ct);
        VaultNote? best = null;
        var bestScore = 0.0;
        foreach (var note in notes)
        {
            var score = Similarity(note.Body, candidate.Content);
            if (score > bestScore)
            {
                bestScore = score;
                best = note;
            }
        }

        if (best is null)
            return null;
        if (IsSubsumed(best.Body, candidate.Content))
            return best;
        return bestScore >= 0.5 ? best : null;
    }

    private string? ResolvePath(MemoryCandidate candidate, AuthorizationEnvelope envelope)
    {
        if (candidate.TargetPath is not null)
        {
            return envelope.VaultPathPrefixes.Any(p =>
                candidate.TargetPath.StartsWith(p, StringComparison.Ordinal))
                ? candidate.TargetPath
                : null;
        }

        var prefix = envelope.VaultPathPrefixes.FirstOrDefault();
        if (prefix is null)
            return null;

        var slug = string.Join("-", candidate.Content
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ':', ';', '"', '\'', '(', ')', '/'],
                StringSplitOptions.RemoveEmptyEntries)
            .Take(5)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(w => w.Length > 0));
        return prefix + slug + ".md";
    }

    internal static bool Contradicts(string existing, string candidate)
    {
        var a = existing.ToLowerInvariant();
        var b = candidate.ToLowerInvariant();
        return AntonymPairs.Any(pair =>
            (a.Contains(pair[0], StringComparison.Ordinal) && b.Contains(pair[1], StringComparison.Ordinal))
            || (a.Contains(pair[1], StringComparison.Ordinal) && b.Contains(pair[0], StringComparison.Ordinal)));
    }

    internal static double Similarity(string a, string b)
    {
        var left = Words(a);
        var right = Words(b);
        if (left.Count == 0 || right.Count == 0)
            return 0;
        var shared = left.Intersect(right).Count();
        return (double)shared / Math.Max(left.Count, right.Count);
    }

    internal static bool IsSubsumed(string existing, string candidate)
    {
        var a = Normalized(existing);
        var b = Normalized(candidate);
        return a.Length > 0 && b.Length > 0 && (b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal));
    }

    private static string Normalized(string text) =>
        string.Join(" ", text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    internal static List<MemoryCandidate> ParseCandidates(string outputText)
    {
        const string fence = "```json";
        var start = outputText.IndexOf(fence, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = outputText.IndexOf("```", start + fence.Length, StringComparison.Ordinal);
            if (end >= 0)
            {
                var parsed = ParseCandidateDocument(
                    outputText.Substring(start + fence.Length, end - start - fence.Length));
                if (parsed.Count > 0)
                    return parsed;
            }
        }

        // Fall back to the whole output: a model that returns raw JSON when asked
        // for a fenced block is still usable; garbage still parses to nothing.
        return ParseCandidateDocument(outputText);
    }

    private static List<MemoryCandidate> ParseCandidateDocument(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("candidates", out var array)
                || array.ValueKind != JsonValueKind.Array)
                return [];

            var candidates = new List<MemoryCandidate>();
            foreach (var element in array.EnumerateArray())
            {
                if (!element.TryGetProperty("content", out var content)
                    || string.IsNullOrWhiteSpace(content.GetString()))
                    continue;
                var evidence = new List<MemorySourceEvidence>();
                if (element.TryGetProperty("evidence", out var items)
                    && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        evidence.Add(new MemorySourceEvidence(
                            item.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "file" : "file",
                            item.TryGetProperty("identifier", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                            item.TryGetProperty("version", out var version) ? version.GetString() : null));
                    }
                }

                candidates.Add(new MemoryCandidate(
                    content.GetString()!,
                    evidence,
                    element.TryGetProperty("targetPath", out var target) ? target.GetString() : null));
            }

            return candidates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static NoteFrontmatter DefaultFrontmatter(string content, DateTimeOffset now)
    {
        var title = content.Split(['.', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            ?? "Untitled";
        if (title.Length > 80)
            title = title[..80];
        return new NoteFrontmatter(title, "reference", "engineering", "active", now, ["orchestrator"]);
    }

    private static string FirstWords(string content, int count) =>
        string.Join(" ", content
            .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Take(count));

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ':', ';', '"', '\'', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
}
