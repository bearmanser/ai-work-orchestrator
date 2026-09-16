using Orchestrator.Application.Identity;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Slack;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Messaging;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;
using static Orchestrator.Application.Tests.MessageClassifierTests;

namespace Orchestrator.Application.Tests;

public sealed class TaskIntakeServiceTests
{
    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", null, "magnus@example.com", true);

    private static readonly Guid DefaultRepositoryId = Guid.CreateVersion7();

    [Fact]
    public async Task Intake_ValidRequestFromAKnownPerson_CreatesOneTaskInPlanning()
    {
        var harness = new Harness();
        var result = await harness.Service.IntakeAsync(Mention("add a GET endpoint"), Magnus, default);

        result.TaskId.ShouldNotBeNull();
        (await harness.LoadTaskAsync(result.TaskId!.Value)).State.ShouldBe(TaskState.Planning);
    }

    [Fact]
    public async Task Intake_SameSlackMessageTwice_CreatesOneTask()
    {
        var harness = new Harness();
        var inbound = Mention("add a GET endpoint");
        await harness.Service.IntakeAsync(inbound, Magnus, default);
        var second = await harness.Service.IntakeAsync(inbound, Magnus, default);

        second.TaskId.ShouldBeNull();
        (await harness.CountTasksAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Intake_SetsCorrelationIdFromChannelAndMessageTs()
    {
        var harness = new Harness();
        var result = await harness.Service.IntakeAsync(
            Mention("x", channel: "C1", ts: "1737000000.000100"), Magnus, default);

        (await harness.LoadTaskAsync(result.TaskId!.Value)).CorrelationId.ShouldBe("slack:C1:1737000000.000100");
    }

    [Fact]
    public async Task Intake_CreatesAnAuthorizationEnvelopeBoundToTheRequester()
    {
        var harness = new Harness();
        var before = DateTimeOffset.UtcNow;
        var result = await harness.Service.IntakeAsync(Mention("x"), Magnus, default);
        var envelope = await harness.LoadEnvelopeAsync(result.TaskId!.Value);

        envelope.RequesterId.ShouldBe(Magnus.Id.ToString());
        envelope.ExpiresAt.ShouldBeInRange(before.AddDays(7), DateTimeOffset.UtcNow.AddDays(7));
    }

    [Fact]
    public async Task Intake_FromAnUnmappedSlackUser_CreatesNoTaskAndExplainsWhy()
    {
        var harness = new Harness();
        var result = await harness.Service.IntakeAsync(Mention("x", userId: "U-stranger"), requester: null!, default);

        result.TaskId.ShouldBeNull();
        result.Reply.ShouldContain("not recognised");
    }

    [Fact]
    public async Task Intake_WhenIntakeIsPaused_CreatesNoTaskAndSaysSo()
    {
        var harness = new Harness();
        await harness.Operations.PauseIntakeAsync("magnus", default);
        var result = await harness.Service.IntakeAsync(Mention("x"), Magnus, default);

        result.TaskId.ShouldBeNull();
        result.Reply.ShouldContain("paused");
    }

    [Fact]
    public async Task HandleSlackEvent_TaskRequestFromKnownPerson_CreatesTask()
    {
        var harness = new Harness();
        var handler = new SlackEventStepHandler(
            harness.Service, new IdentityResolver(new SinglePersonStore(Magnus)));

        var result = await handler.HandleSlackEventAsync(
            Mention("add a GET endpoint", userId: "U-magnus"), default);

        result.TaskId.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleSlackEvent_ChannelChatter_CreatesNothing()
    {
        var harness = new Harness();
        var handler = new SlackEventStepHandler(
            harness.Service, new IdentityResolver(new SinglePersonStore(Magnus)));

        var result = await handler.HandleSlackEventAsync(PlainMessage("lunch at 12?"), default);

        result.TaskId.ShouldBeNull();
        (await harness.CountTasksAsync()).ShouldBe(0);
    }

    private sealed class Harness
    {
        public InMemoryTaskIntakeStore Store { get; } = new();
        public InMemoryOperationalControls Operations { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();

        public TaskIntakeService Service => new(
            Store,
            Operations,
            Audit,
            TimeProvider.System,
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

        public Task<WorkTask> LoadTaskAsync(Guid id) => Store.GetTaskAsync(id);

        public Task<int> CountTasksAsync() => Store.CountTasksAsync();

        public Task<AuthorizationEnvelope> LoadEnvelopeAsync(Guid taskId) => Store.GetEnvelopeAsync(taskId);
    }

    private sealed class InMemoryTaskIntakeStore : ITaskIntakeStore
    {
        private readonly Dictionary<Guid, WorkTask> _tasks = [];
        private readonly Dictionary<string, Guid> _byCorrelation = [];
        private readonly Dictionary<Guid, AuthorizationEnvelope> _envelopes = [];
        private readonly object _gate = new();

        public Task<bool> ExistsForCorrelationAsync(string correlationId, CancellationToken ct)
        {
            lock (_gate)
                return Task.FromResult(_byCorrelation.ContainsKey(correlationId));
        }

        public Task<Guid?> TryCreateAsync(TaskIntakeGraph graph, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_byCorrelation.ContainsKey(graph.Task.CorrelationId))
                    return Task.FromResult<Guid?>(null);

                _tasks[graph.Task.Id] = graph.Task;
                _byCorrelation[graph.Task.CorrelationId] = graph.Task.Id;
                _envelopes[graph.Task.Id] = graph.Envelope;
                return Task.FromResult<Guid?>(graph.Task.Id);
            }
        }

        public Task<WorkTask> GetTaskAsync(Guid id)
        {
            lock (_gate)
                return Task.FromResult(_tasks[id]);
        }

        public Task<int> CountTasksAsync()
        {
            lock (_gate)
                return Task.FromResult(_tasks.Count);
        }

        public Task<AuthorizationEnvelope> GetEnvelopeAsync(Guid taskId)
        {
            lock (_gate)
                return Task.FromResult(_envelopes[taskId]);
        }
    }

    private sealed class InMemoryOperationalControls : IOperationalControls
    {
        private bool _paused;

        public Task PauseIntakeAsync(string actor, CancellationToken ct)
        {
            _paused = true;
            return Task.CompletedTask;
        }

        public Task ResumeIntakeAsync(string actor, CancellationToken ct)
        {
            _paused = false;
            return Task.CompletedTask;
        }

        public Task<bool> IsIntakePausedAsync(CancellationToken ct) => Task.FromResult(_paused);
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

    private sealed class SinglePersonStore(PersonIdentity who) : IPersonStore
    {
        public Task AddAsync(PersonIdentity person, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PersonIdentity?> FindBySlackUserIdAsync(string slackUserId, CancellationToken ct) =>
            Task.FromResult<PersonIdentity?>(
                slackUserId == who.SlackUserId ? who : null);

        public Task<PersonIdentity?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<PersonIdentity?>(id == who.Id ? who : null);

        public Task<IReadOnlyList<PersonIdentity>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PersonIdentity>>([who]);

        public Task RemoveAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
