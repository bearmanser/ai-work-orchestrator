using System.Text.Json;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class HumanQuestionServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider Clock;
    private readonly FakeChatGateway Chat;
    private readonly InMemoryQuestionStore Questions;
    private readonly InMemoryTaskStore Tasks;
    private readonly InMemoryStepLeases Steps;
    private readonly InMemoryPersonStore People;
    private readonly RecordingAuditWriter Audit;
    private readonly HumanQuestionService Service;
    private readonly QuestionEscalationJob Job;

    private readonly PersonIdentity Peer;
    private readonly PersonIdentity Stranger;
    private readonly PersonIdentity Magnus;

    private readonly BudgetLimits Limits = new(
        MaxUsd: 100m,
        MaxWallClock: TimeSpan.FromHours(1),
        MaxAgentInvocations: 10,
        MaxCorrectionAttempts: 10);

    public HumanQuestionServiceTests()
    {
        Clock = new ManualTimeProvider(Start);
        Chat = new FakeChatGateway();
        Questions = new InMemoryQuestionStore();
        Tasks = new InMemoryTaskStore();
        Steps = new InMemoryStepLeases();
        People = new InMemoryPersonStore();
        Audit = new RecordingAuditWriter();

        Peer = new PersonIdentity(Guid.CreateVersion7(), "Peer", "U-peer", "peer-ado", "peer@example.com", true);
        Stranger = new PersonIdentity(Guid.CreateVersion7(), "Stranger", "U-stranger", "stranger-ado", "stranger@example.com", true);
        Magnus = new PersonIdentity(Guid.CreateVersion7(), "Magnus", "U-magnus", "magnus-ado", "magnus@example.com", true);
        People.AddAsync(Peer, default).GetAwaiter().GetResult();
        People.AddAsync(Stranger, default).GetAwaiter().GetResult();
        People.AddAsync(Magnus, default).GetAwaiter().GetResult();

        var gateway = new ToolGateway(
            new PolicyEngine(new RecordingAuditWriter(), PolicyRules.Default),
            new InMemoryToolCallStore(),
            new RecordingAuditWriter(),
            Clock);

        Service = new HumanQuestionService(
            Questions, Tasks, Steps, People, gateway, Chat,
            new IdentityResolver(People), Audit, Clock, new WorkingDayCalculator(new HashSet<DateOnly>()));
        Job = new QuestionEscalationJob(Questions, Tasks, People, Chat, Clock);
    }

    [Fact]
    public async Task Ask_PostsToTheRecipientAndMovesTaskToWaitingForHuman()
    {
        var (task, stepId) = CreateTask();
        var question = await AskAsync(task, stepId, Peer, "Should cancelled orders be included?");

        Chat.Posted.Single().Text.ShouldContain("Should cancelled orders be included?");
        (await LoadTaskAsync(question.TaskId)).State.ShouldBe(TaskState.WaitingForHuman);
    }

    [Fact]
    public async Task Ask_RecipientNotInTheEnvelopeContactList_IsRefused()
    {
        var (task, stepId) = CreateTask();

        await Should.ThrowAsync<PolicyDeniedException>(() => AskAsync(task, stepId, Stranger, "q"));
        Chat.Posted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ask_ReleasesTheStepRatherThanHoldingAnAgentSession()
    {
        var (task, stepId) = CreateTask();

        var question = await AskAsync(task, stepId, Peer, "q");

        (await LoadStepAsync(question.StepId)).LeaseOwner.ShouldBeNull();
    }

    [Fact]
    public async Task RecordAnswer_FromTheAskedPerson_IsAcceptedAndResumesPlanning()
    {
        var (task, stepId) = CreateTask();
        var question = await AskAsync(task, stepId, Peer, "q");
        var outcome = await Service.RecordAnswerAsync(question.ChannelId, question.ThreadTs,
            Peer.SlackUserId!, "yes, include them", Now, default);

        outcome.Accepted.ShouldBeTrue();
        (await LoadTaskAsync(question.TaskId)).State.ShouldBe(TaskState.Planning);
    }

    [Fact]
    public async Task RecordAnswer_FromSomeoneElseInTheThread_IsRecordedButDoesNotResume()
    {
        var (task, stepId) = CreateTask();
        var question = await AskAsync(task, stepId, Peer, "q");
        var outcome = await Service.RecordAnswerAsync(question.ChannelId, question.ThreadTs,
            "U-bystander", "I think yes", Now, default);

        outcome.Accepted.ShouldBeFalse();
        outcome.Reason.ShouldContain("was not asked");
        (await LoadTaskAsync(question.TaskId)).State.ShouldBe(TaskState.WaitingForHuman);
    }

    [Fact]
    public async Task RecordAnswer_ForAnUnknownThread_IsIgnored()
    {
        var outcome = await Service.RecordAnswerAsync("C-other", "9999.0001", Peer.SlackUserId!, "hi", Now, default);

        outcome.Accepted.ShouldBeFalse();
        outcome.QuestionId.ShouldBeNull();
    }

    [Fact]
    public async Task RecordAnswer_AfterTheTaskWasCancelled_IsRecordedWithoutResuming()
    {
        var (task, stepId) = CreateTask();
        var question = await AskAsync(task, stepId, Peer, "q");
        await CancelTaskAsync(question.TaskId);

        var outcome = await Service.RecordAnswerAsync(question.ChannelId, question.ThreadTs,
            Peer.SlackUserId!, "late answer", Now, default);

        outcome.Accepted.ShouldBeFalse();
        (await LoadQuestionAsync(question.Id)).AnswerText.ShouldBe("late answer"); // kept as evidence
    }

    [Fact]
    public async Task EscalationJob_AfterOneWorkingDay_SendsAReminderOnce()
    {
        var (task, stepId) = CreateTask();
        var question = await AskAsync(task, stepId, Peer, "q");
        Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        await Job.RunAsync(default);
        await Job.RunAsync(default);

        Chat.Posted.Count(m => m.Text.Contains("still waiting")).ShouldBe(1);
        (await LoadQuestionAsync(question.Id)).Status.ShouldBe(HumanQuestionStatus.ReminderSent);
    }

    [Fact]
    public async Task EscalationJob_AfterThreeDays_NotifiesTheTaskOwner()
    {
        var (task, stepId) = CreateTask();
        _ = await AskAsync(task, stepId, Peer, "q");
        Clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        await Job.RunAsync(default);

        Chat.Posted.ShouldContain(m => m.RecipientSlackUserId == Magnus.SlackUserId);
    }

    [Fact]
    public async Task EscalationJob_AfterFiveDays_SuspendsTheTask()
    {
        var (task, stepId) = CreateTask();
        var question = await AskAsync(task, stepId, Peer, "q");
        Clock.Advance(TimeSpan.FromDays(5) + TimeSpan.FromMinutes(1));
        await Job.RunAsync(default);

        (await LoadTaskAsync(question.TaskId)).State.ShouldBe(TaskState.Suspended);
    }

    [Fact]
    public async Task EscalationJob_NeverTreatsSilenceAsAnAnswer()
    {
        var (task, stepId) = CreateTask();
        var question = await AskAsync(task, stepId, Peer, "q");
        Clock.Advance(TimeSpan.FromDays(10));
        await Job.RunAsync(default);

        (await LoadQuestionAsync(question.Id)).AnswerText.ShouldBeNull();
        (await LoadTaskAsync(question.TaskId)).State.ShouldNotBe(TaskState.Ready);
    }

    private DateTimeOffset Now => Clock.GetUtcNow();

    private (WorkTask Task, Guid StepId) CreateTask()
    {
        var now = Clock.GetUtcNow();
        var task = new WorkTask(
            "Triage cancelled orders",
            Magnus.Id.ToString(),
            TaskSource.Slack,
            $"slack:C-peer:{Guid.CreateVersion7():N}",
            now);
        task.Fire(TaskTrigger.PlanningStarted, now);
        Tasks.Seed(task);

        var stepId = Guid.CreateVersion7();
        Steps.SeedLease(stepId, task.Id, "agent-1");
        return (task, stepId);
    }

    private AuthorizationEnvelope EnvelopeFor(WorkTask task)
    {
        var now = Clock.GetUtcNow();
        return new AuthorizationEnvelope(
            task.Id,
            Magnus.Id.ToString(),
            [],
            ["staging"],
            ["KAMELEON-TEST"],
            ["codex"],
            [Orchestrator.Domain.Authorization.AgentStage.Implementation],
            ["C-peer"],
            [Peer.SlackUserId!],
            [],
            [],
            Limits,
            now.AddDays(7),
            now.AddMinutes(-1));
    }

    private Task<HumanQuestion> AskAsync(WorkTask task, Guid stepId, PersonIdentity recipient, string text) =>
        Service.AskAsync(
            new AskRequest(
                task.Id,
                stepId,
                recipient.Id,
                text,
                "C-peer",
                "orchestrator",
                EnvelopeFor(task),
                new BudgetLedger(task.Id, Limits)),
            default);

    private Task<WorkTask> LoadTaskAsync(Guid taskId)
    {
        var task = Tasks.Get(taskId);
        task.ShouldNotBeNull();
        return Task.FromResult(task);
    }

    private Task<StepLeaseInfo> LoadStepAsync(Guid stepId)
    {
        var lease = Steps.Get(stepId);
        lease.ShouldNotBeNull();
        return Task.FromResult(lease);
    }

    private Task<HumanQuestion> LoadQuestionAsync(Guid questionId)
    {
        var question = Questions.Get(questionId);
        question.ShouldNotBeNull();
        return Task.FromResult(question);
    }

    private Task CancelTaskAsync(Guid taskId)
    {
        var task = Tasks.Get(taskId);
        task.ShouldNotBeNull();
        task.Fire(TaskTrigger.Cancelled, Clock.GetUtcNow());
        return Task.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeChatGateway : IChatGateway
    {
        private int _counter;

        public List<ChatMessage> Posted { get; } = [];

        public Task<PostedMessage> PostAsync(ChatMessage message, CancellationToken ct)
        {
            Posted.Add(message);
            _counter++;
            var ts = $"172900000{_counter}.000{_counter}";
            return Task.FromResult(new PostedMessage(message.ChannelId, ts, message.ThreadTs ?? ts));
        }

        public Task<PostedMessage> PostBlocksAsync(
            string channelId, string? threadTs, IReadOnlyList<ChatBlock> blocks, string fallbackText, CancellationToken ct) =>
            PostAsync(new ChatMessage(channelId, threadTs, null, fallbackText), ct);
    }

    private sealed class InMemoryQuestionStore : IHumanQuestionStore
    {
        private readonly Dictionary<Guid, HumanQuestion> _questions = [];

        public void Seed(HumanQuestion question) => _questions[question.Id] = question;

        public HumanQuestion? Get(Guid id) => _questions.GetValueOrDefault(id);

        public Task AddAsync(HumanQuestion question, CancellationToken ct)
        {
            _questions[question.Id] = question;
            return Task.CompletedTask;
        }

        public Task<HumanQuestion?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Get(id));

        public Task<HumanQuestion?> FindByChannelThreadAsync(string channelId, string threadTs, CancellationToken ct) =>
            Task.FromResult(_questions.Values.FirstOrDefault(q =>
                string.Equals(q.ChannelId, channelId, StringComparison.Ordinal)
                && string.Equals(q.ThreadTs, threadTs, StringComparison.Ordinal)));

        public Task<IReadOnlyList<HumanQuestion>> ListOpenAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HumanQuestion>>(_questions.Values
                .Where(q => q.Status is HumanQuestionStatus.Open
                    or HumanQuestionStatus.ReminderSent
                    or HumanQuestionStatus.Escalated)
                .ToList());

        public Task SaveAsync(HumanQuestion question, CancellationToken ct)
        {
            _questions[question.Id] = question;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryTaskStore : IQuestionTaskStore
    {
        private readonly Dictionary<Guid, WorkTask> _tasks = [];

        public void Seed(WorkTask task) => _tasks[task.Id] = task;

        public WorkTask? Get(Guid id) => _tasks.GetValueOrDefault(id);

        public Task<WorkTask?> GetAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(Get(taskId));

        public Task SaveAsync(WorkTask task, CancellationToken ct)
        {
            _tasks[task.Id] = task;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryStepLeases : IQuestionStepLeases
    {
        private readonly Dictionary<Guid, StepLeaseInfo> _leases = [];

        public void SeedLease(Guid stepId, Guid taskId, string? leaseOwner) =>
            _leases[stepId] = new StepLeaseInfo(stepId, taskId, leaseOwner);

        public StepLeaseInfo? Get(Guid stepId) => _leases.GetValueOrDefault(stepId);

        public Task<StepLeaseInfo?> GetAsync(Guid stepId, CancellationToken ct) =>
            Task.FromResult(Get(stepId));

        public Task ReleaseAsync(Guid stepId, CancellationToken ct)
        {
            if (_leases.TryGetValue(stepId, out var lease))
                _leases[stepId] = lease with { LeaseOwner = null };
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task RecordAsync(AuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryToolCallStore : IToolCallStore
    {
        private readonly List<ToolCallRecord> _records = [];

        public Task<ToolCallRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(_records.FirstOrDefault(r => r.IdempotencyKey == idempotencyKey));

        public Task<ToolCallRecord> RecordIntentAsync(ToolCall call, PolicyDecision decision, DateTimeOffset now, CancellationToken ct)
        {
            var record = new ToolCallRecord(
                Guid.CreateVersion7(),
                call.TaskId,
                call.StepId,
                call.OperationId,
                call.IdempotencyKey,
                ToolCallOutcome.Unknown,
                decision,
                null,
                now,
                null);
            _records.Add(record);
            return Task.FromResult(record);
        }

        public Task RecordSuccessAsync<TResult>(Guid recordId, TResult? value, DateTimeOffset now, CancellationToken ct)
        {
            var index = _records.FindIndex(r => r.Id == recordId);
            var existing = _records[index];
            var json = value is null ? null : JsonSerializer.Serialize(value);
            _records[index] = existing with
            {
                Outcome = ToolCallOutcome.Succeeded,
                ResultJson = json,
                CompletedAt = now,
            };
            return Task.CompletedTask;
        }

        public TResult? Deserialize<TResult>(ToolCallRecord record) =>
            record.ResultJson is null ? default : JsonSerializer.Deserialize<TResult>(record.ResultJson);
    }
}
