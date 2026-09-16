using System.Text.RegularExpressions;
using Orchestrator.Application.Ports;

namespace Orchestrator.Application.Meetings;

public enum SegmentKind
{
    Information,
    Decision,
    ExplicitAssignment,
    AmbiguousDiscussion,
}

/// <summary>One classified transcript segment, always with its supporting passage.</summary>
public sealed record SegmentClassification(
    SegmentKind Kind,
    double Confidence,
    string SupportingPassage);

/// <summary>
/// Classifies one transcript segment. The service takes this port so tests
/// can stub confidence; production uses <see cref="TranscriptClassifier"/>.
/// </summary>
public interface ITranscriptClassifier
{
    SegmentClassification Classify(TranscriptSegment segment);
}

/// <summary>
/// Deterministic, rule-based classification. Transcript text is evidence,
/// never instruction: anything shaped like a prompt-injection or system
/// directive is treated as speech (ambiguous discussion), never acted on.
/// An explicit assignment requires the addressee to resolve to the
/// configured meeting identity plus an imperative shape; assignments to
/// anyone else, bare imperatives, and speculation go to review or information.
/// </summary>
public sealed class TranscriptClassifier(MeetingTranscriptOptions? options = null) : ITranscriptClassifier
{
    private readonly string meetingIdentity = options?.MeetingIdentity ?? "Orchestrator";

    private static readonly string[] DecisionMarkers =
    [
        "we agreed", "we've agreed", "agreed to", "we decided", "we've decided",
        "decided to", "consensus", "resolved to", "decision:",
    ];

    private static readonly string[] SpeculationMarkers =
    [
        "maybe", "perhaps", "possibly", "might ", " could ", "should we",
        "what if", "not sure", "wondering", "sometime", "one day",
    ];

    private static readonly string[] InjectionMarkers =
    [
        "you are now", "ignore previous", "ignore all previous", "disregard previous",
        "grant all permissions", "admin mode",
    ];

    private static readonly string[] RolePrefixes = ["system:", "developer:", "assistant:"];

    private static readonly string[] ImperativeVerbs =
    [
        "prepare", "create", "fix", "look", "review", "update", "add", "send",
        "write", "implement", "check", "investigate", "draft", "schedule",
        "cancel", "deploy", "merge", "build", "test", "document", "confirm",
        "handle", "own", "drive", "follow", "share", "post", "file", "open",
        "close", "verify", "validate", "refactor", "design", "plan",
        "coordinate", "arrange", "book", "invite", "remind", "ensure",
        "make", "get", "set", "start", "run", "take", "keep",
    ];

    public SegmentClassification Classify(TranscriptSegment segment)
    {
        var text = segment.Text.Trim();

        // Evidence, never instruction: a role-prefixed directive or injected
        // instruction shape is speech to be reviewed, not something to obey.
        if (StartsWithRolePrefix(text) || ContainsMarker(text, InjectionMarkers))
            return new SegmentClassification(SegmentKind.AmbiguousDiscussion, 0.5, segment.Text);

        var (addressee, remainder) = SplitAddressee(text);

        if (addressee is not null
            && string.Equals(addressee, meetingIdentity, StringComparison.OrdinalIgnoreCase)
            && IsImperative(remainder))
            return new SegmentClassification(SegmentKind.ExplicitAssignment, 0.9, segment.Text);

        if (ContainsMarker(text, DecisionMarkers))
            return new SegmentClassification(SegmentKind.Decision, 0.85, segment.Text);

        if (ContainsMarker(text, SpeculationMarkers))
            return new SegmentClassification(SegmentKind.AmbiguousDiscussion, 0.55, segment.Text);

        // Imperative but no resolvable addressee: ambiguous ownership, review it.
        if (addressee is null && IsImperative(text))
            return new SegmentClassification(SegmentKind.AmbiguousDiscussion, 0.55, segment.Text);

        // Directed at someone else, or a plain statement of fact: information.
        return new SegmentClassification(SegmentKind.Information, 0.7, segment.Text);
    }

    private static bool StartsWithRolePrefix(string text) =>
        RolePrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsMarker(string text, string[] markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits a leading addressee ("Anna, ...", "Orchestrator: ...",
    /// "@orchestrator ...") from the rest. Only single-token prefixes count;
    /// "We agreed to ..." must not parse as an addressee.
    /// </summary>
    private static (string? Addressee, string Remainder) SplitAddressee(string text)
    {
        var match = Regex.Match(text, @"^\s*@?([A-Za-z0-9_.\-]+)\s*[:,]\s*(.+)$");
        if (!match.Success)
            return (null, text);

        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    private static bool IsImperative(string text)
    {
        var lowered = text.TrimStart().ToLowerInvariant();
        foreach (var lead in (string[])["please ", "can you ", "could you ", "would you "])
        {
            if (lowered.StartsWith(lead, StringComparison.Ordinal))
                lowered = lowered[lead.Length..].TrimStart();
        }

        var first = lowered.Split([' ', ',', '.', '!', '?', ';', ':'],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && ImperativeVerbs.Contains(first, StringComparer.Ordinal);
    }
}
