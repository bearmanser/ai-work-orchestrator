using Orchestrator.Application.Approvals;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class PlanConfirmationServiceTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid BackendRepoId = Guid.CreateVersion7();
    private static readonly Guid FrontendRepoId = Guid.CreateVersion7();

    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", null, "magnus@example.com", true);

    private static readonly PersonIdentity Stranger = new(
        Guid.CreateVersion7(), "Stranger", "U-stranger", null, "stranger@example.com", true);

    [Fact]
    public async Task PostForConfirmation_MovesTaskToWaitingForPlanConfirmation()
    {
        var harness = new Harness();
        var revision = await harness.Service.PostForConfirmationAsync(TaskId, Content(), default);

        (await harness.LoadTaskAsync(TaskId)).State.ShouldBe(TaskState.WaitingForPlanConfirmation);
        revision.RevisionNumber.ShouldBe(1);
    }

    [Fact]
    public async Task PostForConfirmation_WithUnresolvedOpenQuestions_IsRefused()
    {
        var harness = new Harness();

        // Blocking questions are resolved before a plan is offered for confirmation.
        await Should.ThrowAsync<InvalidOperationException>(
            () => harness.Service.PostForConfirmationAsync(TaskId, Content(openQuestions: ["who owns this?"]), default));
    }

    [Fact]
    public async Task PostForConfirmation_OffersConfirmRequestChangesAndCancel()
    {
        var harness = new Harness();
        await harness.Service.PostForConfirmationAsync(TaskId, Content(), default);

        var actions = harness.Chat.PostedBlocks.Single().ActionIds;
        actions.ShouldBe(["confirm_plan", "request_plan_changes", "cancel_task"], ignoreOrder: true);
    }

    [Fact]
    public async Task Confirm_ByAnEligibleConfirmerOnTheCurrentRevision_DispatchesImplementation()
    {
        var harness = new Harness();
        var revision = await harness.Service.PostForConfirmationAsync(TaskId, Content(), default);
        var outcome = await harness.Service.HandleInteractionAsync(
            Interaction("confirm_plan", revision.ContentHash, Magnus.SlackUserId!), default);

        outcome.Accepted.ShouldBeTrue();
        // Ready is transient: NextStepScheduler (Task 50) fires WorkDispatched itself right after
        // PlanConfirmed, since nothing else in production does - see its remarks. Confirming a
        // plan must therefore land the task in Running, with an implementation attempt already
        // dispatched, not sitting in Ready forever.
        (await harness.LoadTaskAsync(TaskId)).State.ShouldBe(TaskState.Running);
    }

    [Fact]
    public async Task Confirm_ByAnIneligiblePerson_IsRefusedAndTaskStaysWaiting()
    {
        var harness = new Harness();
        var revision = await harness.Service.PostForConfirmationAsync(TaskId, Content(), default);
        var outcome = await harness.Service.HandleInteractionAsync(
            Interaction("confirm_plan", revision.ContentHash, Stranger.SlackUserId!), default);

        outcome.Accepted.ShouldBeFalse();
        (await harness.LoadTaskAsync(TaskId)).State.ShouldBe(TaskState.WaitingForPlanConfirmation);
    }

    [Fact]
    public async Task Confirm_AgainstAStaleRevisionHash_IsRefused()
    {
        var harness = new Harness();
        await harness.Service.PostForConfirmationAsync(TaskId, Content(objective: "v1"), default);
        var current = await harness.Service.PostForConfirmationAsync(TaskId, Content(objective: "v2"), default);

        var outcome = await harness.Service.HandleInteractionAsync(
            Interaction("confirm_plan", "sha256:the-old-one", Magnus.SlackUserId!), default);

        outcome.Accepted.ShouldBeFalse();
        outcome.Reason.ShouldContain("revision");
        current.ConfirmationStatus.ShouldBe(ConfirmationStatus.Pending);
    }

    [Fact]
    public async Task RequestChanges_ReturnsTaskToPlanningAndSupersedesTheRevision()
    {
        var harness = new Harness();
        var revision = await harness.Service.PostForConfirmationAsync(TaskId, Content(), default);
        await harness.Service.HandleInteractionAsync(
            Interaction("request_plan_changes", revision.ContentHash, Magnus.SlackUserId!, "narrow the scope"), default);

        (await harness.LoadTaskAsync(TaskId)).State.ShouldBe(TaskState.Planning);
        (await harness.LoadRevisionAsync(revision.Id)).ConfirmationStatus.ShouldBe(ConfirmationStatus.ChangesRequested);
    }

    [Fact]
    public async Task Cancel_MovesTaskToCancelled()
    {
        var harness = new Harness();
        var revision = await harness.Service.PostForConfirmationAsync(TaskId, Content(), default);
        await harness.Service.HandleInteractionAsync(
            Interaction("cancel_task", revision.ContentHash, Magnus.SlackUserId!), default);

        (await harness.LoadTaskAsync(TaskId)).State.ShouldBe(TaskState.Cancelled);
    }

    [Fact]
    public async Task Confirm_TwiceOnTheSameRevision_SecondIsANoOp()
    {
        var harness = new Harness();
        var revision = await harness.Service.PostForConfirmationAsync(TaskId, Content(), default);
        await harness.Service.HandleInteractionAsync(
            Interaction("confirm_plan", revision.ContentHash, Magnus.SlackUserId!), default);
        var second = await harness.Service.HandleInteractionAsync(
            Interaction("confirm_plan", revision.ContentHash, Magnus.SlackUserId!), default);

        second.Accepted.ShouldBeFalse();
        second.Reason.ShouldContain("already");
    }

    [Fact]
    public async Task Confirm_AfterBaselineDrift_IsRefusedBecauseTheHashNoLongerMatches()
    {
        var harness = new Harness();
        var revision = await harness.Service.PostForConfirmationAsync(TaskId, Content(baseline: "abc"), default);
        await harness.SimulateBaselineMoveAsync(BackendRepoId, newHead: "def");

        var outcome = await harness.Service.HandleInteractionAsync(
            Interaction("confirm_plan", revision.ContentHash, Magnus.SlackUserId!), default);

        outcome.Accepted.ShouldBeFalse();
        outcome.Reason.ShouldContain("baseline");
    }

    [Fact]
    public async Task PlanMessage_ListsEveryRepositoryAndWhetherItIsChangedOrReferenceOnly()
    {
        var harness = new Harness();
        await harness.Service.PostForConfirmationAsync(TaskId,
            Content(repositories: [Change("backend", BackendRepoId), ReferenceOnly("frontend", FrontendRepoId)]), default);

        var text = harness.Chat.PostedBlocks.Single().AsPlainText();
        text.ShouldContain("backend — will change");
        text.ShouldContain("frontend — reference only");
    }

    private static PlanContent Content(
        string objective = "Add ordering endpoint",
        string baseline = "abc",
        IReadOnlyList<string>? scope = null,
        IReadOnlyList<PlannedRepositoryChange>? repositories = null,
        IReadOnlyList<string>? openQuestions = null) => new(
        Objective: objective,
        Scope: scope ?? ["api"],
        Exclusions: ["deployments"],
        Approach: "Add a controller plus tests",
        RepositoryChanges: repositories ?? [Change("backend", BackendRepoId, baseline)],
        Tests: ["dotnet test"],
        Risks: [],
        OpenQuestions: openQuestions ?? [],
        Budget: new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3),
        Providers: [],
        P1Checks: []);

    private static PlannedRepositoryChange Change(string name, Guid id, string baseline = "abc") =>
        new(id, name, ChangeIntent.Change, baseline);

    private static PlannedRepositoryChange ReferenceOnly(string name, Guid id) =>
        new(id, name, ChangeIntent.ReferenceOnly, "abc");

    private static SlackInteraction Interaction(
        string actionId, string revisionHash, string slackUserId, string? text = null) =>
        new(actionId, revisionHash, slackUserId, text);

    private sealed class Harness
    {
        public InMemoryPlanConfirmationStore Store { get; } = new();
        public FakeChatGateway Chat { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();
        public FakeProfileStore Profiles { get; } = new();

        private readonly InMemoryApprovalStore _approvals = new();
        private readonly InMemoryPersonStore _people = new();
        private readonly InMemoryOwnershipStore _ownership = new();

        private PlanConfirmationService? _service;

        public PlanConfirmationService Service => _service ??= BuildService();

        private PlanConfirmationService BuildService()
        {
            foreach (var person in new[] { Magnus, Stranger })
                _people.AddAsync(person, default).GetAwaiter().GetResult();
            foreach (var entry in new[]
            {
                new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository,
                    BackendRepoId.ToString(), Magnus.Id, CanConfirmPlans: true, CanApproveRestrictedActions: true),
                new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository,
                    FrontendRepoId.ToString(), Magnus.Id, CanConfirmPlans: true, CanApproveRestrictedActions: true),
            })
                _ownership.AddAsync(entry, default).GetAwaiter().GetResult();
            Profiles.Seed(
                Profile(BackendRepoId, "backend", "abc"),
                Profile(FrontendRepoId, "frontend", "abc"));
            Store.SeedTask(NewPlanningTask());

            return new PlanConfirmationService(
                Store,
                new ApprovalService(_approvals, Audit),
                new OwnershipDirectory(_people, _ownership),
                new IdentityResolver(_people),
                Profiles,
                Chat,
                Audit,
                new NextStepScheduler(new NoOpStepStore(), new NoOpOutboxWriter(), TimeProvider.System),
                TimeProvider.System,
                new PlanConfirmationOptions { FallbackChannelId = "C1" });
        }

        public Task<WorkTask> LoadTaskAsync(Guid id) => Store.GetTaskAsync(id, default);

        public Task<PlanRevision> LoadRevisionAsync(Guid id) => Store.GetRevisionAsync(id, default);

        public Task SimulateBaselineMoveAsync(Guid repositoryId, string newHead)
        {
            Profiles.MoveBaseline(repositoryId, newHead);
            return Task.CompletedTask;
        }

        private static WorkTask NewPlanningTask()
        {
            var task = new WorkTask("objective", Magnus.Id.ToString(), TaskSource.Slack,
                "slack:C1:1737000000.000100", DateTimeOffset.UtcNow);
            typeof(WorkTask).GetProperty(nameof(WorkTask.Id))!.SetValue(task, TaskId);
            task.Fire(TaskTrigger.PlanningStarted, DateTimeOffset.UtcNow);
            return task;
        }
    }

    private sealed class PostedBlocksMessage(IReadOnlyList<ChatBlock> Blocks)
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

    private sealed class FakeChatGateway : IChatGateway
    {
        public List<PostedBlocksMessage> PostedBlocks { get; } = [];

        public Task<PostedMessage> PostAsync(ChatMessage message, CancellationToken ct) =>
            Task.FromResult(new PostedMessage(message.ChannelId, "1000.0001", message.ThreadTs ?? "1000.0001"));

        public Task<PostedMessage> PostBlocksAsync(
            string channelId, string? threadTs, IReadOnlyList<ChatBlock> blocks, string fallbackText,
            CancellationToken ct)
        {
            PostedBlocks.Add(new PostedBlocksMessage(blocks));
            return Task.FromResult(new PostedMessage(channelId, "1000.0002", threadTs ?? "1000.0002"));
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

    private sealed class InMemoryPlanConfirmationStore : IPlanConfirmationStore
    {
        private readonly Dictionary<Guid, WorkTask> _tasks = [];
        private readonly Dictionary<Guid, PlanRevision> _revisions = [];

        public void SeedTask(WorkTask task) => _tasks[task.Id] = task;

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

        public Task<PlanRevision?> FindByHashAsync(string contentHash, CancellationToken ct) =>
            Task.FromResult(_revisions.Values
                .FirstOrDefault(r => r.ContentHash == contentHash));

        public Task<IReadOnlyList<PlanRevision>> ListRevisionsAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PlanRevision>>(
                _revisions.Values.Where(r => r.TaskId == taskId).OrderBy(r => r.RevisionNumber).ToList());
    }

    private static RepositoryProfile Profile(Guid id, string name, string commit)
    {
        var profile = new RepositoryProfile(
            name, $"https://dev.azure.com/kameleon/Platform/_git/{name}", "main",
            "test", "U-owner", RepositoryAccess.Read,
            new BuildCommands(null, null, null, null, ".", TimeSpan.FromMinutes(5)), []);
        profile.RecordInspection(commit, DateTimeOffset.UtcNow);
        typeof(RepositoryProfile).GetProperty(nameof(RepositoryProfile.Id))!.SetValue(profile, id);
        return profile;
    }

    private sealed class FakeProfileStore : IRepositoryProfileStore
    {
        private readonly Dictionary<Guid, RepositoryProfile> _profiles = [];

        public void Seed(params RepositoryProfile[] profiles)
        {
            foreach (var profile in profiles)
                _profiles[profile.Id] = profile;
        }

        public void MoveBaseline(Guid id, string commit) =>
            _profiles[id].RecordInspection(commit, DateTimeOffset.UtcNow);

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
