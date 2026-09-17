using System.Text.Json;
using Orchestrator.Application.Agents;
using Orchestrator.Application.Approvals;
using Orchestrator.Application.Context;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Context;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class PlanningStepHandlerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid BackendId = Guid.CreateVersion7();
    private static readonly Guid FrontendId = Guid.CreateVersion7();

    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", null, "magnus@example.com", true);

    private static readonly PersonIdentity Requester = new(
        Guid.CreateVersion7(), "Requester", "U-requester", null, "requester@example.com", true);

    [Fact]
    public async Task Handle_AgentProposesAPlanWithNoQuestions_PostsItForConfirmation()
    {
        var harness = new Harness();
        harness.Agent.Returns(PlanProposalJson(questions: []));
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.PlanPosted);
        harness.Chat.PostedBlocks.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Handle_AgentProposesAQuestion_AsksItAndWaits()
    {
        var harness = new Harness();
        harness.Agent.Returns(PlanProposalJson(questions: [Question("Should cancelled orders be included?", "orders")]));
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.BlockingQuestionRaised);
        harness.Chat.Posted.Single().Text.ShouldContain("cancelled orders");
    }

    [Fact]
    public async Task Handle_QuestionWithNoResolvableOwner_AsksTheTaskRequesterInstead()
    {
        var harness = new Harness();
        harness.Agent.Returns(PlanProposalJson(questions: [Question("who owns billing?", domain: "unknown-domain")]));
        await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Chat.Posted.Single().RecipientSlackUserId.ShouldBe(Requester.SlackUserId);
    }

    [Fact]
    public async Task Handle_PlanNamingARepositoryOutsideTheEnvelope_DoesNotPostIt()
    {
        var harness = new Harness();
        harness.Agent.Returns(PlanProposalJson(changeTargets: [BackendId, Guid.CreateVersion7()]));
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.BlockingQuestionRaised);
        harness.Chat.Posted.Single().Text.ShouldContain("outside");
        harness.Chat.PostedBlocks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AgentReturnsUnparseableOutput_FailsTheStepWithoutPosting()
    {
        var harness = new Harness();
        harness.Agent.Returns("I think we should probably just refactor everything.");
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldContain("plan");
        harness.Chat.PostedBlocks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AgentReturnsAFailedResult_FailsTheStep()
    {
        var harness = new Harness();
        harness.Agent.ReturnsFailure("codex exploded");
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.FailureReason.ShouldNotBeNullOrEmpty();
        harness.Chat.PostedBlocks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_ReplanAfterChangesRequested_ProducesRevisionTwo()
    {
        var harness = new Harness();
        harness.Agent.Returns(PlanProposalJson());
        await harness.Handler.HandleAsync(Context(TaskId), default);
        await harness.RequestChangesAsync("narrow the scope");

        harness.Agent.Returns(PlanProposalJson(objective: "narrower"));
        await harness.Handler.HandleAsync(Context(TaskId), default);

        (await harness.LatestRevisionAsync()).RevisionNumber.ShouldBe(2);
    }

    [Fact]
    public async Task Handle_RecordsTheContextSnapshotAgainstThePlanRevision()
    {
        var harness = new Harness();
        harness.Agent.Returns(PlanProposalJson());
        await harness.Handler.HandleAsync(Context(TaskId), default);

        (await harness.LatestRevisionAsync()).ContextSnapshotId.ShouldNotBeNull();
    }

    private static StepContext Context(Guid taskId) =>
        new(taskId, Guid.CreateVersion7(), "planning", null);

    private static string PlanProposalJson(
        string objective = "Add ordering endpoint",
        IReadOnlyList<Guid>? changeTargets = null,
        IReadOnlyList<(string Question, string? Domain)>? questions = null)
    {
        changeTargets ??= [BackendId];
        var changes = changeTargets.Select((id, i) => new
        {
            repositoryId = id,
            repositoryName = i == 0 ? "backend" : "elsewhere",
            intent = "Change",
            baselineCommit = "abc",
        }).ToArray();
        var qs = (questions ?? []).Select(q => new { question = q.Question, domain = q.Domain }).ToArray();

        return "Here is the plan:\n```json\n" + JsonSerializer.Serialize(new
        {
            objective,
            scope = new[] { "api" },
            exclusions = Array.Empty<string>(),
            approach = "Add a controller plus tests",
            repositoryChanges = changes,
            tests = new[] { "dotnet test" },
            risks = Array.Empty<string>(),
            openQuestions = Array.Empty<string>(),
            questions = qs,
        }) + "\n```";
    }

    private static (string Question, string? Domain) Question(string text, string? domain = null) => (text, domain);

    private sealed class Harness
    {
        public FakeAgent Agent { get; } = new();
        public FakeChatGateway Chat { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();
        public FakePlanStore Store { get; }

        private readonly InMemoryApprovalStore _approvals = new();
        private readonly InMemoryPersonStore _people = new();
        private readonly InMemoryOwnershipStore _ownership = new();
        private readonly FakeProfileStore _profiles = new();
        private readonly FakeRelationshipStore _relationships = new();
        private readonly FakeWorkspaceFiles _files = new();
        private readonly FakeSnapshotStore _snapshots = new();
        private readonly FakeArtifactStore _artifacts = new();
        private readonly FakeQuestionPorts _questionPorts = new();

        private PlanningStepHandler? _handler;

        public Harness()
        {
            _people.AddAsync(Magnus, default).GetAwaiter().GetResult();
            _people.AddAsync(Requester, default).GetAwaiter().GetResult();
            _ownership.AddAsync(new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository,
                BackendId.ToString(), Magnus.Id, CanConfirmPlans: true, CanApproveRestrictedActions: true),
                default).GetAwaiter().GetResult();
            _profiles.Seed(
                Profile(BackendId, "backend", ["CLAUDE.md"]),
                Profile(FrontendId, "frontend", ["README.md"]));
            _files.Seed(BackendId, new Dictionary<string, string> { ["CLAUDE.md"] = "rules" });

            var task = NewPlanningTask();
            var envelope = Envelope();
            var ledger = new BudgetLedger(TaskId, Limits());
            Store = new FakePlanStore(task, envelope, ledger);
            _questionPorts.CurrentTask = task;
        }

        public PlanningStepHandler Handler => _handler ??= new PlanningStepHandler(
            Store,
            new ContextAssembler(
                _profiles, _relationships, _files, vault: null, _snapshots, _artifacts, Store, TimeProvider.System),
            new AgentRouter([Agent], new FakeGitWorkspaces(), _artifacts),
            new HumanQuestionService(
                _questionPorts, _questionPorts, _questionPorts, _people,
                new ToolGateway(
                    new PolicyEngine(Audit, PolicyRules.Default, new OpenOperationalControls()),
                    new InMemoryToolCallStore(), Audit, TimeProvider.System),
                Chat, new IdentityResolver(_people), Audit, TimeProvider.System,
                new WorkingDayCalculator(new HashSet<DateOnly>())),
            new PlanConfirmationService(
                Store, new ApprovalService(_approvals, Audit),
                new OwnershipDirectory(_people, _ownership), new IdentityResolver(_people),
                _profiles, Chat, Audit,
                new NextStepScheduler(new NoOpStepStore(), new NoOpOutboxWriter(), TimeProvider.System),
                TimeProvider.System,
                new PlanConfirmationOptions { FallbackChannelId = "C1" }),
            new OwnershipDirectory(_people, _ownership),
            _people,
            new PlanningOptions());

        public Task<PlanRevision> LatestRevisionAsync() => Store.LatestRevisionAsync(TaskId);

        public async Task RequestChangesAsync(string text)
        {
            var revision = await LatestRevisionAsync();
            var confirmations = new PlanConfirmationService(
                Store, new ApprovalService(_approvals, Audit),
                new OwnershipDirectory(_people, _ownership), new IdentityResolver(_people),
                _profiles, Chat, Audit,
                new NextStepScheduler(new NoOpStepStore(), new NoOpOutboxWriter(), TimeProvider.System),
                TimeProvider.System,
                new PlanConfirmationOptions { FallbackChannelId = "C1" });
            await confirmations.HandleInteractionAsync(
                new SlackInteraction("request_plan_changes", revision.ContentHash, Magnus.SlackUserId!, text), default);
        }

        private static BudgetLimits Limits() => new(25m, TimeSpan.FromHours(4), 10, 3);

        private static WorkTask NewPlanningTask()
        {
            var task = new WorkTask("Add ordering endpoint", Requester.Id.ToString(), TaskSource.Slack,
                "slack:C1:1737000000.000100", DateTimeOffset.UtcNow);
            typeof(WorkTask).GetProperty(nameof(WorkTask.Id))!.SetValue(task, TaskId);
            task.Fire(TaskTrigger.PlanningStarted, DateTimeOffset.UtcNow);
            return task;
        }

        private static AuthorizationEnvelope Envelope() => new(
            TaskId,
            Requester.Id.ToString(),
            new[] { BackendId, FrontendId }.Select(id =>
                new RepositoryScope(id, ["*"], ["/"], RepositoryAccess.Read)).ToList(),
            ["staging"],
            [],
            ["replay"],
            [Domain.Authorization.AgentStage.Implementation],
            ["C1"],
            [Magnus.SlackUserId!, Requester.SlackUserId!],
            [],
            [],
            Limits(),
            DateTimeOffset.UtcNow.AddDays(7),
            DateTimeOffset.UtcNow);

        private static RepositoryProfile Profile(Guid id, string name, IReadOnlyList<string> instructionFiles)
        {
            var profile = new RepositoryProfile(
                name, $"https://dev.azure.com/kameleon/Platform/_git/{name}", "main",
                "test", "U-owner", RepositoryAccess.Read,
                new BuildCommands(null, null, null, null, ".", TimeSpan.FromMinutes(5)),
                instructionFiles);
            profile.RecordInspection("abc", DateTimeOffset.UtcNow);
            typeof(RepositoryProfile).GetProperty(nameof(RepositoryProfile.Id))!.SetValue(profile, id);
            return profile;
        }
    }

    private sealed class FakeAgent : ICodingAgent
    {
        private Func<AgentRequest, AgentResult>? _behavior;

        public string Name => "replay";
        public int Invocations { get; private set; }

        public void Returns(string outputText) => _behavior = request =>
            new AgentResult(AgentStatus.Succeeded, "replay", "replay-model", "abc", null, null,
                [], [], [], new AgentUsage(0m, TimeSpan.Zero), null, outputText);

        public void ReturnsFailure(string detail) => _behavior = _ =>
            new AgentResult(AgentStatus.Failed, "replay", "replay-model", "abc", null, null,
                [], [], [], new AgentUsage(0m, TimeSpan.Zero), detail, null);

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(_behavior is null
                ? throw new InvalidOperationException("FakeAgent has no scripted behavior.")
                : _behavior(request));
        }
    }

    private sealed class FakeChatGateway : IChatGateway
    {
        public List<ChatMessage> Posted { get; } = [];
        public List<PostedBlocks> PostedBlocks { get; } = [];

        public Task<PostedMessage> PostAsync(ChatMessage message, CancellationToken ct)
        {
            Posted.Add(message);
            return Task.FromResult(new PostedMessage(message.ChannelId, "1000.0001", message.ThreadTs ?? "1000.0001"));
        }

        public Task<PostedMessage> PostBlocksAsync(
            string channelId, string? threadTs, IReadOnlyList<ChatBlock> blocks, string fallbackText,
            CancellationToken ct)
        {
            PostedBlocks.Add(new PostedBlocks(blocks));
            return Task.FromResult(new PostedMessage(channelId, "1000.0002", threadTs ?? "1000.0002"));
        }
    }

    private sealed class PostedBlocks(IReadOnlyList<ChatBlock> Blocks)
    {
        public IReadOnlyList<string> ActionIds => Blocks
            .OfType<ChatBlock.ActionsBlock>()
            .SelectMany(b => b.Actions)
            .Select(a => a.ActionId)
            .ToList();

        public string AsPlainText() => string.Join("\n", Blocks
            .OfType<ChatBlock.SectionBlock>()
            .Select(b => b.Markdown));
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

    private sealed class FakePlanStore : IPlanConfirmationStore, IPlanningScopeStore
    {
        private readonly Dictionary<Guid, WorkTask> _tasks = [];
        private readonly Dictionary<Guid, PlanRevision> _revisions = [];
        private readonly Dictionary<Guid, AuthorizationEnvelope> _envelopes = [];
        private readonly Dictionary<Guid, BudgetLedger> _ledgers = [];

        public FakePlanStore(WorkTask task, AuthorizationEnvelope envelope, BudgetLedger ledger)
        {
            _tasks[task.Id] = task;
            _envelopes[task.Id] = envelope;
            _ledgers[task.Id] = ledger;
        }

        public Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct) => Task.FromResult(_tasks[taskId]);

        public Task SaveTaskAsync(WorkTask task, CancellationToken ct)
        {
            _tasks[task.Id] = task;
            return Task.CompletedTask;
        }

        public Task AddRevisionAsync(PlanRevision revision, CancellationToken ct)
        {
            _revisions[revision.Id] = revision;
            return Task.CompletedTask;
        }

        public Task<PlanRevision> GetRevisionAsync(Guid revisionId, CancellationToken ct) =>
            Task.FromResult(_revisions[revisionId]);

        public Task<PlanRevision?> GetLatestPendingRevisionAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(_revisions.Values
                .Where(r => r.TaskId == taskId && r.ConfirmationStatus == ConfirmationStatus.Pending)
                .OrderByDescending(r => r.RevisionNumber)
                .FirstOrDefault());

        public Task<IReadOnlyList<PlanRevision>> ListRevisionsAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PlanRevision>>(
                _revisions.Values.Where(r => r.TaskId == taskId).OrderBy(r => r.RevisionNumber).ToList());

        public Task<PlanRevision?> FindByHashAsync(string contentHash, CancellationToken ct) =>
            Task.FromResult(_revisions.Values.FirstOrDefault(r => r.ContentHash == contentHash));

        public Task<AuthorizationEnvelope?> GetEnvelopeAsync(Guid taskId, CancellationToken ct)
        {
            _envelopes.TryGetValue(taskId, out var envelope);
            return Task.FromResult(envelope);
        }

        public Task<BudgetLedger?> GetLedgerAsync(Guid taskId, CancellationToken ct)
        {
            _ledgers.TryGetValue(taskId, out var ledger);
            return Task.FromResult(ledger);
        }

        public Task SaveLedgerAsync(BudgetLedger ledger, CancellationToken ct) => Task.CompletedTask;

        public Task<PlanRevision> LatestRevisionAsync(Guid taskId) =>
            Task.FromResult(_revisions.Values
                .Where(r => r.TaskId == taskId)
                .OrderByDescending(r => r.RevisionNumber)
                .First());
    }

    private sealed class FakeProfileStore : IRepositoryProfileStore
    {
        private readonly Dictionary<Guid, RepositoryProfile> _profiles = [];

        public void Seed(params RepositoryProfile[] profiles)
        {
            foreach (var profile in profiles)
                _profiles[profile.Id] = profile;
        }

        public Task CreateAsync(RepositoryProfile profile, CancellationToken ct)
        {
            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }

        public Task<RepositoryProfile?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            _profiles.TryGetValue(id, out var profile);
            return Task.FromResult(profile);
        }

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepositoryProfile>>(_profiles.Values.ToList());

        public Task<bool> RemoteExistsAsync(string normalizedRemoteUrl, CancellationToken ct) =>
            Task.FromResult(_profiles.Values.Any(p => p.RemoteUrl == normalizedRemoteUrl));

        public Task UpdateAsync(RepositoryProfile profile, uint expectedVersion, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeRelationshipStore : IRelationshipStore
    {
        public Task AddAsync(RepositoryRelationship relationship, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<RepositoryRelationship?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<RepositoryRelationship?>(null);

        public Task<IReadOnlyList<RepositoryRelationship>> ListForRepositoryAsync(Guid repositoryId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepositoryRelationship>>([]);

        public Task RemoveAsync(RepositoryRelationship relationship, CancellationToken ct) =>
            Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeWorkspaceFiles : IWorkspaceFileReader
    {
        private readonly Dictionary<Guid, Dictionary<string, string>> _trees = [];

        public void Seed(Guid repositoryId, Dictionary<string, string> files) => _trees[repositoryId] = files;

        public Task<IReadOnlyList<string>> ListFilesAsync(Guid repositoryId, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                _trees.TryGetValue(repositoryId, out var tree) ? tree.Keys.ToList() : []);

        public Task<string?> ReadFileAsync(Guid repositoryId, string baseBranch, string path, CancellationToken ct) =>
            Task.FromResult<string?>(_trees.TryGetValue(repositoryId, out var tree)
                && tree.TryGetValue(path, out var content) ? content : null);
    }

    private sealed class FakeSnapshotStore : IContextSnapshotStore
    {
        private readonly Dictionary<Guid, ContextSnapshot> _snapshots = [];

        public Task AddAsync(ContextSnapshot snapshot, CancellationToken ct)
        {
            _snapshots[snapshot.Id] = snapshot;
            return Task.CompletedTask;
        }

        public Task<ContextSnapshot?> GetAsync(Guid snapshotId, CancellationToken ct)
        {
            _snapshots.TryGetValue(snapshotId, out var snapshot);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        public Task<string> PutAsync(ArtifactDescriptor descriptor, Stream content, CancellationToken ct) =>
            Task.FromResult(Guid.CreateVersion7().ToString("N"));

        public Task<Stream> OpenAsync(string artifactId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeGitWorkspaces : IGitWorkspace
    {
        public Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct) =>
            Task.FromResult(new GitDiff("abc", "abc", [], string.Empty));

        public Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DisposeAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class OpenOperationalControls : IOperationalControls
    {
        public Task PauseIntakeAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeIntakeAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> IsIntakePausedAsync(CancellationToken ct) => Task.FromResult(false);
        public Task DisableIntegrationAsync(string name, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task EnableIntegrationAsync(string name, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> IsIntegrationDisabledAsync(string name, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> CancelTaskAsync(Guid taskId, string actor, CancellationToken ct) => Task.FromResult(false);
        public Task RevokeAuthorizationAsync(Guid taskId, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task RetryStepAsync(Guid taskId, Guid stepId, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task<ReconciliationSummary> ReconcileTaskAsync(Guid taskId, string actor, CancellationToken ct) =>
            Task.FromResult(new ReconciliationSummary(0, 0, 0, []));
    }

    private sealed class FakeQuestionPorts : IHumanQuestionStore, IQuestionTaskStore, IQuestionStepLeases
    {
        private readonly Dictionary<Guid, HumanQuestion> _questions = [];

        public WorkTask? CurrentTask { get; set; }

        public Task AddAsync(HumanQuestion question, CancellationToken ct)
        {
            _questions[question.Id] = question;
            return Task.CompletedTask;
        }

        Task<HumanQuestion?> IHumanQuestionStore.GetAsync(Guid id, CancellationToken ct)
        {
            _questions.TryGetValue(id, out var question);
            return Task.FromResult(question);
        }

        public Task<HumanQuestion?> FindByChannelThreadAsync(string channelId, string threadTs, CancellationToken ct) =>
            Task.FromResult(_questions.Values.FirstOrDefault(q =>
                q.ChannelId == channelId && q.ThreadTs == threadTs));

        public Task<IReadOnlyList<HumanQuestion>> ListOpenAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HumanQuestion>>(
                _questions.Values.Where(q => q.Status == HumanQuestionStatus.Open).ToList());

        public Task SaveAsync(HumanQuestion question, CancellationToken ct)
        {
            _questions[question.Id] = question;
            return Task.CompletedTask;
        }

        Task<WorkTask?> IQuestionTaskStore.GetAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(CurrentTask is not null && CurrentTask.Id == taskId ? CurrentTask : null);

        Task IQuestionTaskStore.SaveAsync(WorkTask task, CancellationToken ct) => Task.CompletedTask;

        public Task<StepLeaseInfo?> GetAsync(Guid stepId, CancellationToken ct) =>
            Task.FromResult<StepLeaseInfo?>(null);

        public Task ReleaseAsync(Guid stepId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpStepStore : IStepStore
    {
        public Task AddAsync(Step step, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<Step>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Step>>([]);
    }

    private sealed class NoOpOutboxWriter : IOutboxWriter
    {
        public void Enqueue(string messageType, object payload, Guid? taskId, string idempotencyKey) { }
    }
}
