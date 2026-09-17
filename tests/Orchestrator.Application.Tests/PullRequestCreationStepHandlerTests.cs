using Orchestrator.Application.Evidence;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.PullRequests;
using Orchestrator.Application.Repositories;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class PullRequestCreationStepHandlerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid BackendId = Guid.CreateVersion7();
    private static readonly Guid FrontendId = Guid.CreateVersion7();

    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", "magnus-ado-id", "magnus@example.com", true);

    [Fact]
    public async Task Handle_TwoChangedRepositories_CreatesTwoLinkedPullRequests()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId, FrontendId]);
        await harness.Handler.HandleAsync(Context(TaskId), default);

        var tracked = await harness.TrackedPullRequestsAsync();
        tracked.Count.ShouldBe(2);
        tracked.ShouldAllBe(t => t.IsRequired);
        harness.Host.Created[0].Description.ShouldContain(harness.Host.Created[1].Id.ToString());
        harness.Host.Created[1].Description.ShouldContain(harness.Host.Created[0].Id.ToString());
    }

    [Fact]
    public async Task Handle_RepositoryWithNoActualChanges_GetsNoPullRequest()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId, FrontendId]);
        harness.Git.ReturnsEmptyDiffFor(FrontendId);

        await harness.Handler.HandleAsync(Context(TaskId), default);

        (await harness.TrackedPullRequestsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_AnOpenPrAlreadyExistsForTheBranch_UpdatesItInsteadOfCreatingASecond()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Host.SeedExistingOpenPullRequest(sourceBranch: $"orchestrator/{TaskId}", id: 4711);

        await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Host.CreateCalls.ShouldBe(0);
        harness.Host.DescriptionUpdates.Single().PullRequestId.ShouldBe(4711);
    }

    [Fact]
    public async Task Handle_RetriedAfterAnUncertainCreation_ReconcilesRatherThanDuplicating()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Gateway.SeedUnknownOutcome($"pr:{TaskId}:{BackendId}");
        harness.Host.SeedExistingOpenPullRequest($"orchestrator/{TaskId}", 4711);

        await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Host.CreateCalls.ShouldBe(0);
        (await harness.TrackedPullRequestsAsync()).Single().PullRequestId.ShouldBe(4711);
    }

    [Fact]
    public async Task Handle_PushRejectedByRemote_FailsWithoutCreatingAPullRequest()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Git.RejectPush(BackendId);

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBeNull();
        harness.Host.CreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_Success_ReturnsPullRequestOpened()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.PullRequestOpened);
    }

    [Fact]
    public async Task Handle_AddsConfiguredReviewersFromTheOwnershipDirectory()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Host.Created.Single().RequiredReviewerIds.ShouldContain(Magnus.AzureDevOpsId!);
    }

    [Fact]
    public async Task Handle_PrDescription_DoesNotClaimStagingVerificationForLocalTestsOnly()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId], p1Checks: []);
        await harness.Handler.HandleAsync(Context(TaskId), default);

        harness.Host.Created.Single().Description.ShouldContain("No P1 validation was performed");
    }

    private static StepContext Context(Guid taskId) =>
        new(taskId, Guid.CreateVersion7(), "pull_request.create", null);

    private sealed class Harness
    {
        public FakePullRequestHost Host { get; } = new();
        public FakeGitWorkspaces Git { get; } = new();
        public FakeToolGateway Gateway { get; } = new();
        public FakeEvidenceStore Evidence { get; } = new();
        public FakeTrackingStore Tracking { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();
        public FakeScopeStore Scope { get; }
        public FakeConfirmationStore Confirmations { get; } = new();
        public FakeProfileStore Profiles { get; } = new();

        private readonly InMemoryPersonStore _people = new();
        private readonly InMemoryOwnershipStore _ownership = new();

        public Harness()
        {
            _people.AddAsync(Magnus, default).GetAwaiter().GetResult();
            _ownership.AddAsync(new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository,
                BackendId.ToString(), Magnus.Id, CanConfirmPlans: true, CanApproveRestrictedActions: true),
                default).GetAwaiter().GetResult();
            _ownership.AddAsync(new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository,
                FrontendId.ToString(), Magnus.Id, CanConfirmPlans: true, CanApproveRestrictedActions: true),
                default).GetAwaiter().GetResult();
            Profiles.Seed(
                Profile(BackendId, "backend"),
                Profile(FrontendId, "frontend"));
            Scope = new FakeScopeStore(NewReadyTask(), Envelope(), new BudgetLedger(TaskId, Limits()));
        }

        public PullRequestCreationStepHandler Handler => new(
            Scope,
            Confirmations,
            Profiles,
            Git.Workspaces,
            Git,
            Host,
            Gateway,
            new EvidenceAssembler(Evidence),
            Tracking,
            new OwnershipDirectory(_people, _ownership),
            Audit,
            TimeProvider.System);

        public Task ConfirmPlanAsync(
            IReadOnlyList<Guid> changeTargets,
            IReadOnlyList<string>? providers = null,
            IReadOnlyList<string>? p1Checks = null)
        {
            var content = new PlanContent(
                "Add ordering endpoint", ["api"], [], "Add a controller plus tests",
                changeTargets.Select(id => new PlannedRepositoryChange(id, "backend", ChangeIntent.Change, "abc")).ToList(),
                ["dotnet test"], [], [],
                Limits(),
                providers ?? ["replay"],
                p1Checks ?? []);
            var revision = new PlanRevision(TaskId, 1, content);
            revision.Confirm(Magnus.Id, DateTimeOffset.UtcNow);
            Confirmations.SeedRevision(revision);

            var evidence = new TaskEvidence(
                [new CheckResult("dotnet test", true, 0, TimeSpan.FromMinutes(1), "a1", CheckOrigin.RuntimeExecuted)],
                [],
                new GitDiffSummary("abc", "def", ["src/Orders.cs"], null),
                revision.ContentHash,
                []);
            Evidence.Seed(TaskId, evidence);

            foreach (var id in changeTargets)
                Git.SeedWorkspace(id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PullRequestTracking>> TrackedPullRequestsAsync() =>
            Tracking.ListByTaskAsync(TaskId, default);

        private static BudgetLimits Limits() => new(25m, TimeSpan.FromHours(4), 10, 3);

        private static WorkTask NewReadyTask()
        {
            var task = new WorkTask("objective", Magnus.Id.ToString(), TaskSource.Slack,
                "slack:C1:1737000000.000100", DateTimeOffset.UtcNow);
            typeof(WorkTask).GetProperty(nameof(WorkTask.Id))!.SetValue(task, TaskId);
            task.Fire(TaskTrigger.PlanningStarted, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.PlanPosted, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.PlanConfirmed, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.WorkDispatched, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.ImplementationFinished, DateTimeOffset.UtcNow);
            return task;
        }

        private static AuthorizationEnvelope Envelope() => new(
            TaskId,
            Magnus.Id.ToString(),
            new[] { BackendId, FrontendId }.Select(id =>
                new RepositoryScope(id, ["*"], ["/"], RepositoryAccess.Write)).ToList(),
            ["staging"],
            ["KAMELEON-TEST"],
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

    private sealed class CreatedPr
    {
        public int Id { get; set; }
        public CreatePullRequest Request { get; set; } = null!;
        public string Description { get; set; } = string.Empty;
        public IReadOnlyList<string> RequiredReviewerIds => Request.RequiredReviewerIds;
    }

    private sealed class FakePullRequestHost : IPullRequestHost
    {
        public List<CreatedPr> Created { get; } = [];
        public int CreateCalls { get; private set; }
        public List<(int PullRequestId, string Description)> DescriptionUpdates { get; } = [];

        private readonly Dictionary<(string Repository, string Branch), PullRequestRef> _open = [];
        private int _nextId = 4710;

        public void SeedExistingOpenPullRequest(string sourceBranch, int id, string repository = "backend") =>
            _open[(repository, sourceBranch)] = new PullRequestRef(id,
                $"https://dev.azure.com/kameleon/Platform/_git/{repository}/pullrequest/{id}",
                sourceBranch, "main", "abc");

        public Task<PullRequestRef> CreateAsync(CreatePullRequest request, CancellationToken ct)
        {
            CreateCalls++;
            var pr = new PullRequestRef(++_nextId,
                $"https://dev.azure.com/kameleon/Platform/_git/{request.RepositoryName}/pullrequest/{_nextId}",
                request.SourceBranch, request.TargetBranch, "abc");
            Created.Add(new CreatedPr { Id = pr.Id, Request = request, Description = request.Description });
            _open[(request.RepositoryName, request.SourceBranch)] = pr;
            return Task.FromResult(pr);
        }

        public Task<PullRequestRef?> FindOpenBySourceBranchAsync(
            string organization, string project, string repositoryName, string sourceBranch,
            CancellationToken ct)
        {
            _open.TryGetValue((repositoryName, sourceBranch), out var pr);
            return Task.FromResult(pr);
        }

        public Task<PullRequestDetail> GetAsync(int pullRequestId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ReplyToThreadAsync(int pullRequestId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(int pullRequestId, string description, CancellationToken ct)
        {
            DescriptionUpdates.Add((pullRequestId, description));
            foreach (var created in Created.Where(c => c.Id == pullRequestId))
                created.Description = description;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGitWorkspaces : IGitWorkspace
    {
        private readonly Dictionary<string, GitDiff> _diffs = [];
        private readonly HashSet<Guid> _rejectedPushes = [];

        public FakeWorkspaceStore Workspaces { get; } = new();

        public void SeedWorkspace(Guid repositoryId, string diff = "diff --git a/src/Orders.cs")
        {
            var workspace = new Workspace(TaskId, repositoryId, $"/tmp/{repositoryId:N}",
                "abc", "main", $"orchestrator/{TaskId}", "git.backend.credential", DateTimeOffset.UtcNow);
            Workspaces.Seed(workspace);
            _diffs[workspace.WorkspaceId] = new GitDiff(
                "abc", "def", [new ChangedFile("src/Orders.cs", "Modified")], diff);
        }

        public void ReturnsEmptyDiffFor(Guid repositoryId)
        {
            var id = Workspaces.IdFor(repositoryId);
            _diffs[id] = new GitDiff("abc", "abc", [], string.Empty);
        }

        public void RejectPush(Guid repositoryId) => _rejectedPushes.Add(repositoryId);

        public Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct) =>
            Task.FromResult(_diffs.TryGetValue(workspaceId, out var diff)
                ? diff
                : new GitDiff("abc", "abc", [], string.Empty));

        public Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct)
        {
            if (Workspaces.RepositoryFor(workspaceId) is { } repoId && _rejectedPushes.Contains(repoId))
                throw new HttpRequestException("remote rejected the push");
            return Task.FromResult("def");
        }

        public Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DisposeAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        private readonly Dictionary<string, Workspace> _workspaces = [];

        public void Seed(Workspace workspace) => _workspaces[workspace.WorkspaceId] = workspace;

        public string IdFor(Guid repositoryId) =>
            _workspaces.Values.Single(w => w.RepositoryId == repositoryId).WorkspaceId;

        public Guid? RepositoryFor(string workspaceId) =>
            _workspaces.TryGetValue(workspaceId, out var workspace) ? workspace.RepositoryId : null;

        public Task AddAsync(Workspace workspace, CancellationToken ct)
        {
            _workspaces[workspace.WorkspaceId] = workspace;
            return Task.CompletedTask;
        }

        public Task<Workspace?> GetByIdAsync(string workspaceId, CancellationToken ct)
        {
            _workspaces.TryGetValue(workspaceId, out var workspace);
            return Task.FromResult(workspace);
        }

        public Task RemoveAsync(Workspace workspace, CancellationToken ct)
        {
            _workspaces.Remove(workspace.WorkspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Workspace>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Workspace>>(
                _workspaces.Values.Where(w => w.TaskId == taskId).ToList());
    }

    private sealed class FakeToolGateway : IToolGateway
    {
        private readonly HashSet<string> _unknownKeys = [];

        public void SeedUnknownOutcome(string idempotencyKey) => _unknownKeys.Add(idempotencyKey);

        public async Task<ToolResult<TResult>> InvokeAsync<TResult>(
            ToolCall call, Func<CancellationToken, Task<TResult>> invoke, CancellationToken ct)
        {
            if (_unknownKeys.Contains(call.IdempotencyKey))
                throw new UnreconciledOperationException(call.IdempotencyKey);

            var value = await invoke(ct);
            return new ToolResult<TResult>(true, value, new PolicyDecision.Permitted("test"));
        }
    }

    private sealed class FakeEvidenceStore : ITaskEvidenceStore
    {
        private readonly Dictionary<Guid, TaskEvidence> _evidence = [];

        public void Seed(Guid taskId, TaskEvidence evidence) => _evidence[taskId] = evidence;

        public Task<IReadOnlyList<CheckResult>> GetRuntimeChecksAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CheckResult>>(
                _evidence.TryGetValue(taskId, out var evidence) ? evidence.RuntimeChecks : []);

        public Task<IReadOnlyList<DomainCheckResult>> GetDomainChecksAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DomainCheckResult>>(
                _evidence.TryGetValue(taskId, out var evidence) ? evidence.DomainChecks : []);

        public Task<GitDiffSummary> GetDiffSummaryAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(_evidence.TryGetValue(taskId, out var evidence)
                ? evidence.Diff
                : new GitDiffSummary(string.Empty, string.Empty, [], null));

        public Task<string> GetPlanRevisionHashAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(_evidence.TryGetValue(taskId, out var evidence) ? evidence.PlanRevisionHash : string.Empty);

        public Task<IReadOnlyList<string>> GetUnresolvedQuestionsAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                _evidence.TryGetValue(taskId, out var evidence) ? evidence.UnresolvedQuestions : []);
    }

    private sealed class FakeTrackingStore : IPullRequestTrackingStore
    {
        private readonly Dictionary<Guid, PullRequestTracking> _trackings = [];

        public Task<IReadOnlyList<PullRequestTracking>> ListByTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PullRequestTracking>>(
                _trackings.Values.Where(t => t.TaskId == taskId).ToList());

        public Task UpsertAsync(PullRequestTracking tracking, CancellationToken ct)
        {
            _trackings[tracking.Id] = tracking;
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

    private sealed class FakeScopeStore : IPlanningScopeStore
    {
        private readonly WorkTask _task;
        private readonly AuthorizationEnvelope _envelope;
        private readonly BudgetLedger _ledger;

        public FakeScopeStore(WorkTask task, AuthorizationEnvelope envelope, BudgetLedger ledger)
        {
            _task = task;
            _envelope = envelope;
            _ledger = ledger;
        }

        public Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct) => Task.FromResult(_task);

        public Task SaveTaskAsync(WorkTask task, CancellationToken ct) => Task.CompletedTask;

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
