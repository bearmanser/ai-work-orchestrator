using System.Security.Cryptography;
using System.Text;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Slack;
using Orchestrator.Domain.Memory;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Meetings;

/// <summary>Transcript ingestion tuning: meeting identity and review threshold.</summary>
public sealed class MeetingTranscriptOptions
{
    public string MeetingIdentity { get; set; } = "Orchestrator";

    public double AssignmentConfidenceThreshold { get; set; } = 0.7;
}

/// <summary>Result of processing one transcript upload.</summary>
public sealed record MeetingProcessingResult(
    IReadOnlyList<Guid> CreatedTaskIds,
    IReadOnlyList<MemoryProposal> DecisionNotes,
    IReadOnlyList<SegmentClassification> NeedsReview);

/// <summary>
/// Task read/write access for the correction path (return the affected task
/// to Planning). Production stays on the existing task tables; the only
/// implementation in this task is the process-local in-memory one below.
/// </summary>
public interface IMeetingTaskStore
{
    Task<WorkTask?> GetAsync(Guid taskId, CancellationToken ct);

    Task<WorkTask?> GetByCorrelationAsync(string correlationId, CancellationToken ct);

    Task SaveAsync(WorkTask task, CancellationToken ct);
}

/// <summary>Remembers which segments were processed and what they produced.</summary>
public sealed record ProcessedSegment(
    string MeetingId,
    string SegmentId,
    string Text,
    Guid? TaskId);

/// <summary>Segment ledger used for re-upload dedup and correction detection.</summary>
public interface IMeetingSegmentStore
{
    Task<ProcessedSegment?> GetAsync(string meetingId, string segmentId, CancellationToken ct);

    Task SaveAsync(ProcessedSegment segment, CancellationToken ct);
}

/// <summary>
/// Process-local segment ledger. No migration is allowed in this task, so
/// correction linkage survives only for the process lifetime; re-upload
/// safety itself is durable via <see cref="IEventRegistry"/>.
/// </summary>
public sealed class InMemoryMeetingSegmentStore : IMeetingSegmentStore
{
    private readonly Dictionary<string, ProcessedSegment> segments = [];
    private readonly object gate = new();

    public Task<ProcessedSegment?> GetAsync(string meetingId, string segmentId, CancellationToken ct)
    {
        lock (gate)
            return Task.FromResult(segments.GetValueOrDefault(Key(meetingId, segmentId)));
    }

    public Task SaveAsync(ProcessedSegment segment, CancellationToken ct)
    {
        lock (gate)
            segments[Key(segment.MeetingId, segment.SegmentId)] = segment;
        return Task.CompletedTask;
    }

    private static string Key(string meetingId, string segmentId) => $"{meetingId}:{segmentId}";
}

/// <summary>
/// Process-local task handle for the correction path. Tasks themselves are
/// created durably through <see cref="TaskIntakeService"/>; only the
/// correction linkage is process-local until a durable implementation lands.
/// </summary>
public sealed class InMemoryMeetingTaskStore : IMeetingTaskStore
{
    private readonly Dictionary<Guid, WorkTask> tasks = [];
    private readonly object gate = new();

    public Task<WorkTask?> GetAsync(Guid taskId, CancellationToken ct)
    {
        lock (gate)
            return Task.FromResult(tasks.GetValueOrDefault(taskId));
    }

    public Task<WorkTask?> GetByCorrelationAsync(string correlationId, CancellationToken ct)
    {
        lock (gate)
            return Task.FromResult(tasks.Values.FirstOrDefault(t =>
                string.Equals(t.CorrelationId, correlationId, StringComparison.Ordinal)));
    }

    public Task SaveAsync(WorkTask task, CancellationToken ct)
    {
        lock (gate)
            tasks[task.Id] = task;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Turns approved meeting transcript segments into Planning tasks (explicit
/// assignments only), memory proposals (decisions), or review items.
/// Deterministic guards: an assignment creates a task only when the addressee
/// resolved to the configured meeting identity (via the classifier) and the
/// speaker resolves to a known <see cref="PersonIdentity"/> and confidence is
/// at least 0.7. Anything else goes to <c>NeedsReview</c>; ambiguity never
/// starts work. Re-uploading a transcript is safe via the event registry
/// (<c>teams:{meetingId}:{segmentId}</c>). A segment that corrects an earlier
/// one returns the affected task to Planning and posts a corrected follow-up.
/// </summary>
public sealed class MeetingAssignmentService(
    ITranscriptClassifier classifier,
    TaskIntakeService intake,
    IPersonStore people,
    IMeetingTaskStore tasks,
    IMeetingSegmentStore segments,
    IEventRegistry registry,
    IChatGateway chat,
    TimeProvider clock,
    MeetingTranscriptOptions? options = null)
{
    private readonly MeetingTranscriptOptions settings = options ?? new();

    private static readonly string[] CorrectionMarkers =
    [
        "do not", "don't", "cancel", "never mind", "disregard", "no longer",
        "supersede", "correct",
    ];

    public async Task<MeetingProcessingResult> ProcessAsync(TranscriptUpload upload, CancellationToken ct)
    {
        var created = new List<Guid>();
        var decisions = new List<MemoryProposal>();
        var review = new List<SegmentClassification>();

        foreach (var segment in upload.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
                continue;

            var externalId = $"{upload.MeetingId}:{segment.SegmentId}";
            var payloadHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{externalId}:{segment.Text}")));
            var registration = await registry.TryRegisterAsync("teams", externalId, payloadHash, ct);

            var prior = await segments.GetAsync(upload.MeetingId, segment.SegmentId, ct);
            if (prior is not null)
            {
                if (string.Equals(prior.Text, segment.Text, StringComparison.Ordinal))
                    continue; // identical re-upload

                if (LooksLikeCorrection(segment.Text))
                {
                    await HandleCorrectionAsync(upload, segment, prior, ct);
                    continue;
                }

                // Transcription fix without a negation: remember the new text,
                // but take no new side effects.
                await segments.SaveAsync(prior with { Text = segment.Text }, ct);
                continue;
            }

            if (!registration.IsFirstDelivery)
                continue; // processed before the segment ledger was reset

            var classification = classifier.Classify(segment);
            switch (classification.Kind)
            {
                case SegmentKind.ExplicitAssignment:
                    await HandleAssignmentAsync(upload, segment, classification, created, review, ct);
                    break;
                case SegmentKind.Decision:
                    decisions.Add(new MemoryProposal(
                        Guid.CreateVersion7(), null, segment.Text, [segment.Text], clock.GetUtcNow()));
                    await segments.SaveAsync(
                        new ProcessedSegment(upload.MeetingId, segment.SegmentId, segment.Text, null), ct);
                    break;
                case SegmentKind.AmbiguousDiscussion:
                    review.Add(classification);
                    await segments.SaveAsync(
                        new ProcessedSegment(upload.MeetingId, segment.SegmentId, segment.Text, null), ct);
                    break;
                default:
                    await segments.SaveAsync(
                        new ProcessedSegment(upload.MeetingId, segment.SegmentId, segment.Text, null), ct);
                    break;
            }
        }

        return new MeetingProcessingResult(created, decisions, review);
    }

    private async Task HandleAssignmentAsync(
        TranscriptUpload upload,
        TranscriptSegment segment,
        SegmentClassification classification,
        List<Guid> created,
        List<SegmentClassification> review,
        CancellationToken ct)
    {
        var speaker = await ResolveSpeakerAsync(segment.SpeakerIdentifier, ct);
        if (speaker is null || classification.Confidence < settings.AssignmentConfidenceThreshold)
        {
            review.Add(classification);
            await segments.SaveAsync(
                new ProcessedSegment(upload.MeetingId, segment.SegmentId, segment.Text, null), ct);
            return;
        }

        // Reuse Slack intake so assignment tasks get the same envelope,
        // budget, planning step, and outbox dispatch. The correlation stays
        // unique per meeting segment; no mention prefix is stripped.
        var inbound = new SlackInbound(
            Guid.CreateVersion7().ToString(),
            SlackInboundKind.DirectMessage,
            $"teams:{upload.MeetingId}",
            null,
            segment.SegmentId,
            speaker.SlackUserId ?? speaker.Email ?? segment.SpeakerIdentifier,
            segment.Text);
        var intakeResult = await intake.IntakeAsync(inbound, speaker, ct);
        if (intakeResult.TaskId is null)
            return;

        await segments.SaveAsync(
            new ProcessedSegment(upload.MeetingId, segment.SegmentId, segment.Text, intakeResult.TaskId), ct);
        var tracked = await tasks.GetByCorrelationAsync(
            $"slack:teams:{upload.MeetingId}:{segment.SegmentId}", ct);
        if (tracked is not null)
            await tasks.SaveAsync(tracked, ct);
        created.Add(intakeResult.TaskId.Value);

        await chat.PostAsync(new ChatMessage(
            $"teams:{upload.MeetingId}",
            null,
            speaker.SlackUserId,
            $"Created task {intakeResult.TaskId} from '{upload.MeetingSubject}' " +
            $"(segment {segment.SegmentId}). Please confirm in Slack before any implementation starts."), ct);
    }

    private async Task HandleCorrectionAsync(
        TranscriptUpload upload,
        TranscriptSegment segment,
        ProcessedSegment prior,
        CancellationToken ct)
    {
        await segments.SaveAsync(prior with { Text = segment.Text }, ct);

        if (prior.TaskId is null)
            return;

        var task = await tasks.GetAsync(prior.TaskId.Value, ct);
        if (task is null)
            return;

        ReturnToPlanning(task, clock.GetUtcNow());
        await tasks.SaveAsync(task, ct);

        await chat.PostAsync(new ChatMessage(
            $"teams:{upload.MeetingId}",
            null,
            null,
            $"Task {task.Id} corrected: segment {segment.SegmentId} in meeting " +
            $"'{upload.MeetingSubject}' was corrected. The task is back in Planning; " +
            "any prior confirmation is invalidated."), ct);
    }

    private async Task<Domain.Identity.PersonIdentity?> ResolveSpeakerAsync(
        string speakerIdentifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(speakerIdentifier))
            return null;

        var candidates = await people.ListAsync(ct);
        return candidates.FirstOrDefault(p =>
            p.IsActive
            && (string.Equals(p.Email, speakerIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.SlackUserId, speakerIdentifier, StringComparison.Ordinal)));
    }

    private static bool LooksLikeCorrection(string text) =>
        CorrectionMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Walks the state machine back to Planning along the shortest trigger
    /// path; already-Planning tasks are untouched, terminal ones unmovable.
    /// </summary>
    internal static void ReturnToPlanning(WorkTask task, DateTimeOffset now)
    {
        if (task.State == TaskState.Planning)
            return;

        var triggers = Enum.GetValues<TaskTrigger>();
        var previous = new Dictionary<TaskState, (TaskState From, TaskTrigger Via)>();
        var queue = new Queue<TaskState>();
        queue.Enqueue(task.State);
        previous[task.State] = (task.State, default);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var trigger in triggers)
            {
                if (!TaskStateMachine.TryTransition(current, trigger, out var next))
                    continue;
                if (previous.ContainsKey(next))
                    continue;

                previous[next] = (current, trigger);
                if (next == TaskState.Planning)
                {
                    var path = new Stack<TaskTrigger>();
                    var walker = next;
                    while (!walker.Equals(task.State))
                    {
                        var step = previous[walker];
                        path.Push(step.Via);
                        walker = step.From;
                    }

                    foreach (var hop in path)
                        task.Fire(hop, now);
                    return;
                }

                queue.Enqueue(next);
            }
        }
    }
}
