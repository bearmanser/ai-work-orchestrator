using Orchestrator.Application.Agents;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Policy;
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

public sealed class ImplementationStepHandlerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid BackendId = Guid.CreateVersion7();
    private static readonly Guid FrontendId = Guid.CreateVersion7();

    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", null, "magnus@example.com", true);

    [Fact]
    public async Task Handle_WithoutAConfirmedPlan_RefusesToInvokeTheAgent()
    {
        var harness = new Harness();
        await harness.SetTaskStateAsync(TaskState.Ready, confirmedPlan: false);
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Agent.Invocations.ShouldBe(0);
        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldContain("confirmed plan");
    }

    [Fact]
    public async Task Handle_WithAConfirmedPlan_CreatesOneWorkspacePerChangeTarget()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId, FrontendId]);
        await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Git.PreparedWorkspaces.Select(w => w.RepositoryId).ShouldBe([BackendId, FrontendId], ignoreOrder: true);
    }

    [Fact]
    public async Task Handle_DoesNotCreateWorkspacesForReferenceOnlyRepositories()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId], referenceOnly: [FrontendId]);
        await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Git.PreparedWorkspaces.Select(w => w.RepositoryId).ShouldBe([BackendId]);
    }

    [Fact]
    public async Task Handle_BaselineDriftedSinceConfirmation_ReturnsBaselineDriftDetected()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Profiles.MoveBaseline(BackendId, "moved");

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.BaselineDriftDetected);
        harness.Agent.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_AgentSucceeds_ChargesTheBudgetAndReturnsImplementationFinished()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Agent.Returns(SuccessfulResult(costUsd: 0.75m, duration: TimeSpan.FromMinutes(6)));

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.ImplementationFinished);
        (await harness.LoadLedgerAsync()).SpentUsd.ShouldBe(0.75m);
    }

    [Fact]
    public async Task Handle_BudgetAlreadyExhausted_DoesNotInvokeTheAgent()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        await harness.ExhaustBudgetAsync();

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Agent.Invocations.ShouldBe(0);
        outcome.Trigger.ShouldBe(TaskTrigger.UnrecoverableFailure);
    }

    [Fact]
    public async Task Handle_UnauthorizedProviderInThePlan_IsBlockedBeforeInvocation()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId], providers: ["gemini"]);
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Agent.Invocations.ShouldBe(0);
        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldContain("provider");
    }

    [Fact]
    public async Task Handle_CapturesTheDiffAsAnArtifactEvenWhenTheAgentFails()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Agent.Returns(FailedResultAfterPartialWork());

        await harness.Handler.HandleAsync(Context(TaskId), default);

        (await harness.ArtifactsForAsync()).ShouldContain(a => a.Type == "diff");
    }

    private static StepContext Context(Guid taskId) =>
        new(taskId, Guid.CreateVersion7(), "coding_agent.execute", null);

    private static AgentResult SuccessfulResult(decimal costUsd, TimeSpan duration) => new(
        AgentStatus.Succeeded, "replay", "replay-model", "abc", "def", null,
        [], [], [], new AgentUsage(costUsd, duration), null, null);

    private static AgentResult FailedResultAfterPartialWork() => new(
        AgentStatus.Failed, "replay", "replay-model", "abc", null, null,
        [], [], [], new AgentUsage(0.5m, TimeSpan.FromMinutes(1)), "tests still red", null);

    private sealed class Harness
    {
        public FakeAgent Agent { get; } = new();
        public FakeGitWorkspaces Git { get; } = new();
        public FakeArtifactStore Artifacts { get; } = new();
        public FakeAgentRunStore AgentRuns { get; } = new();
        public FakeScopeStore Scope { get; }
        public FakeConfirmationStore Confirmations { get; } = new();
        public FakeProfileStore Profiles { get; } = new();

        public Harness()
        {
            Profiles.Seed(
                Profile(BackendId, "backend"),
                Profile(FrontendId, "frontend"));
            Scope = new FakeScopeStore(NewReadyTask(), Envelope(), new BudgetLedger(TaskId, Limits()));
        }

        public ImplementationStepHandler Handler => new(
            Scope,
            Confirmations,
            Profiles,
            Git,
            new AgentRouter([Agent], Git, Artifacts),
            Artifacts,
            AgentRuns,
            TimeProvider.System);

        public async Task SetTaskStateAsync(TaskState state, bool confirmedPlan)
        {
            var task = NewTaskInState(state);
            Scope.SeedTask(task);
            if (confirmedPlan)
                await ConfirmPlanAsync([BackendId]);
        }

        public Task ConfirmPlanAsync(
            IReadOnlyList<Guid> changeTargets,
            IReadOnlyList<Guid>? referenceOnly = null,
            IReadOnlyList<string>? providers = null,
            IReadOnlyList<string>? p1Checks = null)
        {
            var content = new PlanContent(
                "Add ordering endpoint", ["api"], [], "Add a controller plus tests",
                changeTargets.Select(id => new PlannedRepositoryChange(id, "backend", ChangeIntent.Change, "abc"))
                    .Concat((referenceOnly ?? []).Select(id =>
                        new PlannedRepositoryChange(id, "frontend", ChangeIntent.ReferenceOnly, "abc")))
                    .ToList(),
                ["dotnet test"], [], [],
                Limits(),
                providers ?? ["replay"],
                p1Checks ?? []);
            var revision = new PlanRevision(TaskId, 1, content);
            revision.Confirm(Magnus.Id, DateTimeOffset.UtcNow);
            Confirmations.SeedRevision(revision);
            return Task.CompletedTask;
        }

        public Task<BudgetLedger> LoadLedgerAsync() => Scope.GetLedger();

        public async Task ExhaustBudgetAsync()
        {
            var ledger = await Scope.GetLedger();
            ledger.Charge(1000m, TimeSpan.Zero, 0);
        }

        public Task<IReadOnlyList<(string Type, Guid TaskId)>> ArtifactsForAsync() =>
            Artifacts.ListForAsync(TaskId);

        private static BudgetLimits Limits() => new(25m, TimeSpan.FromHours(4), 10, 3);

        private static WorkTask NewReadyTask()
        {
            var task = new WorkTask("objective", Magnus.Id.ToString(), TaskSource.Slack,
                "slack:C1:1737000000.000100", DateTimeOffset.UtcNow);
            typeof(WorkTask).GetProperty(nameof(WorkTask.Id))!.SetValue(task, TaskId);
            task.Fire(TaskTrigger.PlanningStarted, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.PlanPosted, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.PlanConfirmed, DateTimeOffset.UtcNow);
            return task;
        }

        private static WorkTask NewTaskInState(TaskState state)
        {
            var task = new WorkTask("objective", Magnus.Id.ToString(), TaskSource.Slack,
                $"slack:C1:{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            typeof(WorkTask).GetProperty(nameof(WorkTask.Id))!.SetValue(task, TaskId);
            task.Fire(TaskTrigger.PlanningStarted, DateTimeOffset.UtcNow);
            if (state == TaskState.Planning)
                return task;
            task.Fire(TaskTrigger.PlanPosted, DateTimeOffset.UtcNow);
            if (state == TaskState.WaitingForPlanConfirmation)
                return task;
            task.Fire(TaskTrigger.PlanConfirmed, DateTimeOffset.UtcNow);
            return task;
        }

        private static AuthorizationEnvelope Envelope() => new(
            TaskId,
            Magnus.Id.ToString(),
            new[] { BackendId, FrontendId }.Select(id =>
                new RepositoryScope(id, ["*"], ["/"], RepositoryAccess.Write)).ToList(),
            ["staging"],
            [],
            ["replay"],
            [Domain.Authorization.AgentStage.Implementation],
            ["C1"],
            [Magnus.SlackUserId!],
            [],
            [],
            Limits(),
            DateTimeOffset.UtcNow.AddDays(7),
            DateTimeOffset.UtcNow);

        private static RepositoryProfile Profile(Guid id, string name)
        {
            var profile = new RepositoryProfile(
                name, $"https://dev.azure.com/kameleon/Platform/_git/{name}", "main",
                "test", "U-owner", RepositoryAccess.Write,
                new BuildCommands("dotnet restore", "dotnet build", "dotnet test", null, ".", TimeSpan.FromMinutes(5)),
                ["CLAUDE.md"]);
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

        public void Returns(AgentResult result) => _behavior = _ => result;

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(_behavior is null
                ? new AgentResult(AgentStatus.Succeeded, "replay", "replay-model", "abc", "def", null,
                    [], [], [], new AgentUsage(0m, TimeSpan.Zero), null, null)
                : _behavior(request));
        }
    }

    private sealed class FakeGitWorkspaces : IGitWorkspace
    {
        public List<WorkspaceRequest> PreparedWorkspaces { get; } = [];
        private readonly HashSet<Guid> _drifted = [];

        public void ReportDrift(Guid repositoryId) => _drifted.Add(repositoryId);

        public Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct)
        {
            PreparedWorkspaces.Add(request);
            return Task.FromResult(new WorkspaceHandle(
                Guid.CreateVersion7().ToString("N"), $"/tmp/{request.RepositoryId:N}",
                "abc", request.BaseBranch, request.WorkBranch));
        }

        public Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct) =>
            Task.FromResult(new GitDiff("abc", "def", [new ChangedFile("src/Orders.cs", "Modified")], "diff --git a/src/Orders.cs"));

        public Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DisposeAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        private readonly List<(ArtifactDescriptor Descriptor, string Content)> _stored = [];

        public Task<string> PutAsync(ArtifactDescriptor descriptor, Stream content, CancellationToken ct)
        {
            using var reader = new StreamReader(content);
            _stored.Add((descriptor, reader.ReadToEnd()));
            return Task.FromResult(Guid.CreateVersion7().ToString("N"));
        }

        public Task<Stream> OpenAsync(string artifactId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<(string Type, Guid TaskId)>> ListForAsync(Guid taskId) =>
            Task.FromResult<IReadOnlyList<(string Type, Guid TaskId)>>(
                _stored.Where(s => s.Descriptor.TaskId == taskId)
                    .Select(s => (s.Descriptor.Type, s.Descriptor.TaskId)).ToList());
    }

    private sealed class FakeAgentRunStore : IAgentRunStore
    {
        private readonly List<AgentRun> _runs = [];

        public Task AddAsync(AgentRun run, CancellationToken ct)
        {
            _runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentRun>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentRun>>(_runs.Where(r => r.TaskId == taskId).ToList());
    }

    private sealed class FakeScopeStore : IPlanningScopeStore
    {
        private WorkTask _task;
        private readonly AuthorizationEnvelope _envelope;
        private readonly BudgetLedger _ledger;

        public FakeScopeStore(WorkTask task, AuthorizationEnvelope envelope, BudgetLedger ledger)
        {
            _task = task;
            _envelope = envelope;
            _ledger = ledger;
        }

        public void SeedTask(WorkTask task) => _task = task;

        public Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct) => Task.FromResult(_task);

        public Task SaveTaskAsync(WorkTask task, CancellationToken ct)
        {
            _task = task;
            return Task.CompletedTask;
        }

        public Task<AuthorizationEnvelope?> GetEnvelopeAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<AuthorizationEnvelope?>(_envelope);

        public Task<BudgetLedger?> GetLedgerAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<BudgetLedger?>(_ledger);

        public Task<BudgetLedger> GetLedger() => Task.FromResult(_ledger);

        public Task SaveLedgerAsync(BudgetLedger ledger, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeConfirmationStore : IPlanConfirmationStore
    {
        private readonly Dictionary<Guid, PlanRevision> _revisions = [];

        public void SeedRevision(PlanRevision revision) => _revisions[revision.Id] = revision;

        public Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SaveTaskAsync(WorkTask task, CancellationToken ct) => Task.CompletedTask;

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
}
