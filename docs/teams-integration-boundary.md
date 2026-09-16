# Teams integration boundary

Live meeting participation is **not** implemented. There is no Azure Bot
Service registration, no real-time audio, no recording, and no automatic
transcription. Ingestion is a manual or scripted upload of an approved
transcript export through `POST /api/admin/transcripts`
(see `TranscriptEndpoints`), which accepts a meeting id, subject, start time,
and an ordered list of segments (segment id, speaker identifier, spoken-at
time, text).

## How ingestion works today

- `TranscriptSegmentParser` turns a pasted plain-text export into segments;
  the endpoint also accepts segments directly as JSON.
- `TranscriptClassifier` applies deterministic rules with the segment fenced
  as untrusted evidence: transcript text is evidence, never instruction.
- `MeetingAssignmentService` creates a Planning task only for explicit
  assignments whose addressee is the configured meeting identity, whose
  speaker resolves to a known person, and whose confidence is at least 0.7.
  Everything uncertain goes to `NeedsReview`; ambiguity never starts work.
- Re-uploads are safe: segments deduplicate through `IEventRegistry` with
  source `teams` and external id `{meetingId}:{segmentId}`.

## Before live participation can be added

A tenant decision is required for each of the following, and none of them
is made here:

1. **Bot identity** — which Azure Bot Service registration joins meetings,
   what it is named, and which tenant it belongs to.
2. **Eligible meetings** — which meetings, channels, or organizers the bot
   may join; default-deny.
3. **Participant notice and consent** — how attendees are told a meeting is
   transcribed, how consent is recorded, and what happens when someone
   declines.
4. **Transcript retention** — where transcripts are stored, for how long,
   who can read them, and when they are deleted.

The application event contract (`TranscriptUpload` / `TranscriptSegment`
behind `ITranscriptSource`) is designed so a live source can implement that
port without changing classification, assignment, or review downstream.
