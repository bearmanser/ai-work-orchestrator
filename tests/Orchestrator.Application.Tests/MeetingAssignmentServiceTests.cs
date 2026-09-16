using Orchestrator.Application.Identity;
using Orchestrator.Application.Meetings;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Slack;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Messaging;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class MeetingAssignmentServiceTests
{
    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", null, "magnus@example.com", true);

    private static readonly Guid DefaultRepositoryId = Guid.CreateVersion7();

    private static readonly DateTimeOffset At = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Process_AnExplicitAssignment_CreatesOnePlanningTask()
    {
        var harness = new Harness();
        var result = await harness.Service.ProcessAsync(Upload(Segment("Orchestrator, prepare a PR for the validation change",
            speaker: Magnus.Email!)), default);

        result.CreatedTaskIds.Count.ShouldBe(1);
        (await harness.LoadTaskAsync(result.CreatedTaskIds[0])).State.ShouldBe(TaskState.Planning);
    }

    [Fact]
    public async Task Process_AssignmentTask_RequiresSlackConfirmationBeforeAnyImplementation()
    {
        var harness = new Harness();
        var result = await harness.Service.ProcessAsync(Upload(AssignmentSegment()), default);
        var task = await harness.LoadTaskAsync(result.CreatedTaskIds[0]);

        task.State.ShouldNotBe(TaskState.Ready);
        harness.Chat.Posted.ShouldNotBeEmpty(); // a Slack follow-up thread exists
    }

    [Fact]
    public async Task Process_AmbiguousDiscussion_CreatesNoTask()
    {
        var harness = new Harness();
        var result = await harness.Service.ProcessAsync(Upload(Segment("Maybe we should redo this sometime")), default);
        result.CreatedTaskIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Process_AnUnresolvableSpeaker_CreatesNoTaskAndFlagsForReview()
    {
        var harness = new Harness();
        var result = await harness.Service.ProcessAsync(Upload(AssignmentSegment(speaker: "unknown@example.com")), default);

        result.CreatedTaskIds.ShouldBeEmpty();
        result.NeedsReview.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Process_TheSameSegmentTwice_CreatesOneTask()
    {
        var harness = new Harness();
        var upload = Upload(AssignmentSegment(segmentId: "seg-1"));
        await harness.Service.ProcessAsync(upload, default);
        var second = await harness.Service.ProcessAsync(upload, default);

        second.CreatedTaskIds.ShouldBeEmpty();
        (await harness.CountTasksAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Process_ADecision_ProducesAMemoryProposalNotATask()
    {
        var harness = new Harness();
        var result = await harness.Service.ProcessAsync(Upload(Segment("We agreed to keep the current API version")), default);

        result.CreatedTaskIds.ShouldBeEmpty();
        result.DecisionNotes.Single().Content.ShouldContain("current API version");
    }

    [Fact]
    public async Task Process_ACorrectedSegment_ReassessesTheAffectedTask()
    {
        var harness = new Harness();
        var first = await harness.Service.ProcessAsync(Upload(AssignmentSegment("seg-1", "prepare a PR for validation")), default);
        await harness.Service.ProcessAsync(UploadCorrecting("seg-1", "actually, do not do the validation change"), default);

        (await harness.LoadTaskAsync(first.CreatedTaskIds[0])).State.ShouldBe(TaskState.Planning);
        harness.Chat.Posted.Last().Text.ShouldContain("corrected");
    }

    [Fact]
    public async Task Process_LowConfidenceAssignment_GoesToReviewRatherThanStartingWork()
    {
        var harness = new Harness();
        harness.Classifier.ReturnConfidence(0.4);
        var result = await harness.Service.ProcessAsync(Upload(AssignmentSegment()), default);

        result.CreatedTaskIds.ShouldBeEmpty();
        result.NeedsReview.ShouldNotBeEmpty();
    }

    private static TranscriptUpload Upload(params TranscriptSegment[] segments) =>
        new("meeting-1", "Weekly sync", At, segments);

    private static TranscriptSegment Segment(string text, string? speaker = null, string segmentId = "seg-1") =>
        new(segmentId, speaker ?? Magnus.Email!, At.AddMinutes(1), text);

    private static TranscriptSegment AssignmentSegment(
        string segmentId = "seg-1",
        string text = "prepare a PR for the validation change",
        string? speaker = null) =>
        // A well-formed assignment segment is addressed to the orchestrator's
        // configured meeting identity and spoken by a known person.
        Segment($"Orchestrator, {text}", speaker, segmentId);

    private static TranscriptUpload UploadCorrecting(string segmentId, string text) =>
        Upload(Segment(text, Magnus.Email!, segmentId));

    private sealed class Harness
    {
        public Harness()
        {
            People = new InMemoryPersonStore();
            People.AddAsync(Magnus, default).GetAwaiter().GetResult();
            Stores = new HarnessStores();
            Classifier = new StubClassifier();
            Chat = new FakeChatGateway();
            Registry = new FakeEventRegistry();
            Clock = new ManualTimeProvider(At);
            Intake = new TaskIntakeService(
                Stores,
                new OpenControls(),
                new RecordingAuditWriter(),
                Clock,
                new TaskIntakeOptions
                {
                    DefaultRepositoryIds = [DefaultRepositoryId],
                    DefaultAllowedProviders = ["codex", "claude"],
                    DefaultBudget = new BudgetLimits(
                        MaxUsd: 25m,
                        MaxWallClock: TimeSpan.FromHours(4),
                        MaxAgentInvocations: 10,
                        MaxCorrectionAttempts: 3),
                });
            Service = new MeetingAssignmentService(
                Classifier, Intake, People, Stores, Stores, Registry, Chat, Clock);
        }

        public InMemoryPersonStore People { get; }

        public HarnessStores Stores { get; }

        public StubClassifier Classifier { get; }

        public FakeChatGateway Chat { get; }

        public FakeEventRegistry Registry { get; }

        public ManualTimeProvider Clock { get; }

        public TaskIntakeService Intake { get; }

        public MeetingAssignmentService Service { get; }

        public Task<WorkTask> LoadTaskAsync(Guid id) => Stores.GetTaskAsync(id);

        public Task<int> CountTasksAsync() => Stores.CountTasksAsync();
    }

    private sealed class StubClassifier : ITranscriptClassifier
    {
        private readonly TranscriptClassifier inner = new();
        private double? fixedConfidence;

        public void ReturnConfidence(double confidence) => fixedConfidence = confidence;

        public SegmentClassification Classify(TranscriptSegment segment)
        {
            var real = inner.Classify(segment);
            return fixedConfidence is null ? real : real with { Confidence = fixedConfidence.Value };
        }
    }

    private sealed class HarnessStores : ITaskIntakeStore, IMeetingTaskStore, IMeetingSegmentStore
    {
        private readonly Dictionary<Guid, WorkTask> tasks = [];
        private readonly Dictionary<string, Guid> byCorrelation = [];
        private readonly Dictionary<string, ProcessedSegment> segments = [];
        private readonly object gate = new();

        public Task<bool> ExistsForCorrelationAsync(string correlationId, CancellationToken ct)
        {
            lock (gate)
                return Task.FromResult(byCorrelation.ContainsKey(correlationId));
        }

        public Task<Guid?> TryCreateAsync(TaskIntakeGraph graph, CancellationToken ct)
        {
            lock (gate)
            {
                if (byCorrelation.ContainsKey(graph.Task.CorrelationId))
                    return Task.FromResult<Guid?>(null);

                tasks[graph.Task.Id] = graph.Task;
                byCorrelation[graph.Task.CorrelationId] = graph.Task.Id;
                return Task.FromResult<Guid?>(graph.Task.Id);
            }
        }

        public Task<WorkTask> GetTaskAsync(Guid id)
        {
            lock (gate)
                return Task.FromResult(tasks[id]);
        }

        public Task<int> CountTasksAsync()
        {
            lock (gate)
                return Task.FromResult(tasks.Count);
        }

        Task<WorkTask?> IMeetingTaskStore.GetAsync(Guid taskId, CancellationToken ct)
        {
            lock (gate)
                return Task.FromResult(tasks.GetValueOrDefault(taskId));
        }

        Task<WorkTask?> IMeetingTaskStore.GetByCorrelationAsync(string correlationId, CancellationToken ct)
        {
            lock (gate)
                return Task.FromResult(
                    byCorrelation.TryGetValue(correlationId, out var id) ? tasks[id] : null);
        }

        Task IMeetingTaskStore.SaveAsync(WorkTask task, CancellationToken ct)
        {
            lock (gate)
                tasks[task.Id] = task;
            return Task.CompletedTask;
        }

        Task<ProcessedSegment?> IMeetingSegmentStore.GetAsync(string meetingId, string segmentId, CancellationToken ct)
        {
            lock (gate)
                return Task.FromResult(segments.GetValueOrDefault($"{meetingId}:{segmentId}"));
        }

        Task IMeetingSegmentStore.SaveAsync(ProcessedSegment segment, CancellationToken ct)
        {
            lock (gate)
                segments[$"{segment.MeetingId}:{segment.SegmentId}"] = segment;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEventRegistry : IEventRegistry
    {
        private readonly HashSet<string> seen = [];
        private readonly object gate = new();

        public Task<EventRegistration> TryRegisterAsync(
            string source, string externalEventId, string payloadHash, CancellationToken ct)
        {
            lock (gate)
            {
                var key = $"{source}:{externalEventId}";
                if (!seen.Add(key))
                    return Task.FromResult(new EventRegistration(false, Guid.CreateVersion7()));
                return Task.FromResult(new EventRegistration(true, Guid.CreateVersion7()));
            }
        }
    }

    private sealed class FakeChatGateway : IChatGateway
    {
        private int counter;

        public List<ChatMessage> Posted { get; } = [];

        public Task<PostedMessage> PostAsync(ChatMessage message, CancellationToken ct)
        {
            Posted.Add(message);
            counter++;
            var ts = $"172900000{counter}.000{counter}";
            return Task.FromResult(new PostedMessage(message.ChannelId, ts, message.ThreadTs ?? ts));
        }

        public Task<PostedMessage> PostBlocksAsync(
            string channelId, string? threadTs, IReadOnlyList<ChatBlock> blocks, string fallbackText, CancellationToken ct) =>
            PostAsync(new ChatMessage(channelId, threadTs, null, fallbackText), ct);
    }

    private sealed class OpenControls : IOperationalControls
    {
        public Task PauseIntakeAsync(string actor, CancellationToken ct) => throw new NotSupportedException();

        public Task ResumeIntakeAsync(string actor, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> IsIntakePausedAsync(CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public Task RecordAsync(AuditEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
