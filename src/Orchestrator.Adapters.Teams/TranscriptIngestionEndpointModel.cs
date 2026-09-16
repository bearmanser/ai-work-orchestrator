using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Teams;

/// <summary>One segment of an admin-uploaded transcript export.</summary>
public sealed record TranscriptSegmentModel(
    string SegmentId,
    string SpeakerIdentifier,
    DateTimeOffset SpokenAt,
    string Text);

/// <summary>
/// Admin-upload DTO for an approved transcript export. Maps onto the
/// application event contract (<see cref="TranscriptUpload"/>) unchanged.
/// </summary>
public sealed record TranscriptUploadRequest(
    string MeetingId,
    string MeetingSubject,
    DateTimeOffset StartedAt,
    IReadOnlyList<TranscriptSegmentModel> Segments)
{
    public TranscriptUpload ToUpload() => new(
        MeetingId,
        MeetingSubject,
        StartedAt,
        Segments
            .Select(s => new TranscriptSegment(s.SegmentId, s.SpeakerIdentifier, s.SpokenAt, s.Text))
            .ToList());
}
