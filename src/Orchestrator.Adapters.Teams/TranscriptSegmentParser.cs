using System.Globalization;
using System.Text.RegularExpressions;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Teams;

/// <summary>
/// Parses a pasted plain-text transcript export into segments. Accepted line
/// shapes (blank lines are skipped):
/// <list type="bullet">
/// <item><c>[mm:ss] Speaker: text</c> or <c>[hh:mm:ss] Speaker: text</c></item>
/// <item><c>Speaker: text</c> (single-token speaker, no whitespace)</item>
/// <item><c>continuation text</c> (attributed to the previous speaker)</item>
/// </list>
/// Lines without a speaker prefix belong to the previous speaker, or
/// <c>"unknown"</c> when there is none. Segment ids are <c>seg-1</c>,
/// <c>seg-2</c>, ...; untimestamped lines keep <paramref name="startedAt"/>.
/// </summary>
public static partial class TranscriptSegmentParser
{
    public static IReadOnlyList<TranscriptSegment> Parse(string rawText, DateTimeOffset startedAt)
    {
        var segments = new List<TranscriptSegment>();
        var speaker = "unknown";
        var index = 0;

        foreach (var line in rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var spokenAt = startedAt;
            var rest = trimmed;
            var timestamp = TimestampPrefix().Match(rest);
            if (timestamp.Success)
            {
                spokenAt = startedAt.Add(TimestampOffset(timestamp));
                rest = rest[timestamp.Length..].TrimStart();
            }

            var colon = rest.IndexOf(':');
            if (colon > 0 && !rest[..colon].Any(char.IsWhiteSpace))
            {
                speaker = rest[..colon].Trim();
                rest = rest[(colon + 1)..].Trim();
            }

            if (rest.Length == 0)
                continue;

            index++;
            segments.Add(new TranscriptSegment($"seg-{index}", speaker, spokenAt, rest));
        }

        return segments;
    }

    private static TimeSpan TimestampOffset(Match timestamp)
    {
        var minutes = int.Parse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(timestamp.Groups[2].Value, CultureInfo.InvariantCulture);
        if (timestamp.Groups[3].Success)
            return new TimeSpan(
                int.Parse(timestamp.Groups[3].Value, CultureInfo.InvariantCulture), minutes, seconds);
        return new TimeSpan(0, minutes, seconds);
    }

    [GeneratedRegex(@"^\[(\d{1,2}):(\d{2})(?::(\d{2}))?\]")]
    private static partial Regex TimestampPrefix();
}
