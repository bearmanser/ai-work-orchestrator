namespace Orchestrator.Application.Ports;

/// <summary>One utterance in an approved meeting transcript export.</summary>
public sealed record TranscriptSegment(
    string SegmentId,
    string SpeakerIdentifier,
    DateTimeOffset SpokenAt,
    string Text);

/// <summary>
/// An approved transcript export for one meeting. This is the event contract:
/// a live meeting source can implement <see cref="ITranscriptSource"/> to
/// produce these without changing anything downstream.
/// </summary>
public sealed record TranscriptUpload(
    string MeetingId,
    string MeetingSubject,
    DateTimeOffset StartedAt,
    IReadOnlyList<TranscriptSegment> Segments);

/// <summary>
/// Provides approved transcript exports. Today the only source is the manual
/// admin upload endpoint; a live Teams participant would implement this port
/// once the tenant decisions in <c>docs/teams-integration-boundary.md</c>
/// are made.
/// </summary>
public interface ITranscriptSource
{
    Task<IReadOnlyList<TranscriptUpload>> ListPendingAsync(CancellationToken ct);
}
