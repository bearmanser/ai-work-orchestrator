using System.Text.RegularExpressions;

namespace Orchestrator.Application.Memory;

public sealed record MemorySourceEvidence(string Kind, string Identifier, string? Version);

public sealed record MemoryCandidate(
    string Content,
    IReadOnlyList<MemorySourceEvidence> Evidence,
    string? TargetPath = null);

public sealed record DurabilityVerdict(bool Durable, string Reason);

/// <summary>
/// Cheap deterministic filters applied after the agent proposes, because the
/// decision to publish is the runtime's, not the model's. Mirrors the
/// secret-shape guard in audit storage; the two must stay in sync.
/// </summary>
public static partial class DurabilityRules
{
    [GeneratedRegex(@"(gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9]{20,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.)")]
    private static partial Regex SecretShape();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}\b")]
    private static partial Regex TimeOfDay();

    private static readonly string[] TranscriptMarkers =
        ["i will", "i ran", "i'll ", "i've ", "let me ", "i am going to"];

    public static DurabilityVerdict IsDurable(MemoryCandidate candidate)
    {
        if (candidate.Evidence.Count == 0)
            return new DurabilityVerdict(false, "No supporting evidence.");

        if (SecretShape().IsMatch(candidate.Content))
            return new DurabilityVerdict(false, "Content looks like it contains a secret value.");

        var lower = candidate.Content.ToLowerInvariant();
        if (TranscriptMarkers.Any(lower.Contains))
            return new DurabilityVerdict(false, "Content is agent transcript narration, not durable knowledge.");

        if (TimeOfDay().IsMatch(candidate.Content) || IsBareCommandOutcome(candidate.Content))
            return new DurabilityVerdict(false, "Content is a raw task log entry, not durable knowledge.");

        return new DurabilityVerdict(true, "Supported by evidence and not excluded.");
    }

    private static bool IsBareCommandOutcome(string content)
    {
        var words = content.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0
            && words[0].Equals("ran", StringComparison.OrdinalIgnoreCase)
            && words.Any(w => w.Equals("passed", StringComparison.OrdinalIgnoreCase)
                || w.Equals("failed", StringComparison.OrdinalIgnoreCase));
    }
}
