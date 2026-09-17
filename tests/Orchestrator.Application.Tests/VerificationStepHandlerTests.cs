using Orchestrator.Application.Agents;
using Orchestrator.Application.Checks;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class VerificationStepHandlerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid BackendId = Guid.CreateVersion7();

    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", null, "magnus@example.com", true);

    [Fact]
    public async Task Handle_AllRuntimeChecksPass_ReturnsPullRequestOpenedPath()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Checks.Returns([Passed("dotnet test")]);

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.PullRequestOpened);
    }

    [Fact]
    public async Task Handle_AgentClaimedPass_NeverSubstitutesForARuntimeRun()
    {
        var harness = new Harness(withCommands: false);
        await harness.ConfirmPlanAsync([BackendId]);
        harness.AgentClaimed([new ClaimedCheck("dotnet test", true)]);
        harness.Checks.Returns([]); // nothing was actually run
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.CorrectionRequired);
    }

    [Fact]
    public async Task Handle_ARuntimeCheckFails_ReturnsCorrectionRequired()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Checks.Returns([Failed("dotnet test")]);

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.CorrectionRequired);
    }

    [Fact]
    public async Task Handle_AgentClaimedPassButRuntimeFailed_RecordsTheMismatchAndCorrects()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.AgentClaimed([new ClaimedCheck("dotnet test", ClaimedPassed: true)]);
        harness.Checks.Returns([Failed("dotnet test")]);

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.CorrectionRequired);
        (await harness.AuditEntriesAsync()).ShouldContain(e => e.Action == "checks.claim-mismatch");
    }

    [Fact]
    public async Task Handle_EmptyDiff_ReturnsNonCodeOutcomeAcceptedRatherThanOpeningAnEmptyPr()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Git.ReturnsEmptyDiff();

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.NonCodeOutcomeAccepted);
    }

    [Fact]
    public async Task Handle_CorrectionAttemptsExhausted_FailsRatherThanLooping()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId]);
        harness.Checks.Returns([Failed("dotnet test")]);
        await harness.SetCorrectionAttemptsAsync(3);

        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.UnrecoverableFailure);
    }

    [Fact]
    public async Task Handle_PlanRequestsP1Validation_RunsItAndRecordsItSeparately()
    {
        var harness = new Harness();
        await harness.ConfirmPlanAsync([BackendId], p1Checks: ["picked-order-count"]);
        harness.Checks.Returns([Passed("dotnet test")]);

        await harness.Handler.HandleAsync(Context(TaskId), default);

        (await harness.EvidenceForAsync()).DomainChecks.Single().QueryName.ShouldBe("picked-order-count");
    }

    private static StepContext Context(Guid taskId) =>
        new(taskId, Guid.CreateVersion7(), "verification", null);

    private static CheckResult Passed(string command) =>
        new(command, true, 0, TimeSpan.FromMinutes(1), "artifact-1", CheckOrigin.RuntimeExecuted);

    private static CheckResult Failed(string command) =>
        new(command, false, 1, TimeSpan.FromMinutes(1), "artifact-2", CheckOrigin.RuntimeExecuted);

    private sealed class Harness
    {
        public FakeCheckExecutor Checks { get; } = new();
        public FakeGitWorkspaces Git { get; } = new();
        public FakeDomainSource P1 { get; } = new();
        public FakeArtifactStore Artifacts { get; } = new();
        public FakeAgentRunStore AgentRuns { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();
        public FakeScopeStore Scope { get; }
        public FakeConfirmationStore Confirmations { get; } = new();
        public FakeProfileStore Profiles { get; } = new();

        public Harness(bool withCommands = true)
        {
            Profiles.Seed(Profile(BackendId, "backend", withCommands));
            Scope = new FakeScopeStore(NewVerifyingTask(), Envelope(), new BudgetLedger(TaskId, Limits()));
        }

        public VerificationStepHandler Handler => new(
            Scope,
            Confirmations,
            Profiles,
            GitWorkspaces(),
            Git,
            new CheckRunner(Checks, Artifacts, new CheckRunnerOptions { Image = "orchestrator/worker:latest" }),
            P1,
            AgentRuns,
            Artifacts,
            Audit);

        private FakeWorkspaceStore GitWorkspaces() => new(BackendId);

        public Task ConfirmPlanAsync(IReadOnlyList<Guid> changeTargets, IReadOnlyList<string>? p1Checks = null)
        {
            var content = new PlanContent(
                "Add ordering endpoint", ["api"], [], "Add a controller plus tests",
                changeTargets.Select(id => new PlannedRepositoryChange(id, "backend", ChangeIntent.Change, "abc")).ToList(),
                ["dotnet test"], [], [],
                Limits(),
                ["replay"],
                p1Checks ?? []);
            var revision = new PlanRevision(TaskId, 1, content);
            revision.Confirm(Magnus.Id, DateTimeOffset.UtcNow);
            Confirmations.SeedRevision(revision);
            return Task.CompletedTask;
        }

        public void AgentClaimed(IReadOnlyList<ClaimedCheck> claims) =>
            AgentRuns.Seed(new AgentRun(TaskId, Guid.CreateVersion7(), BackendId,
                System.Text.Json.JsonSerializer.Serialize(new AgentResult(
                    AgentStatus.Succeeded, "replay", "replay-model", "abc", "def", null,
                    claims, [], [], new AgentUsage(0m, TimeSpan.Zero), null, null)),
                DateTimeOffset.UtcNow));

        public Task<TaskEvidence> EvidenceForAsync() => Artifacts.LatestEvidenceAsync(TaskId);

        public Task<IReadOnlyList<AuditEntry>> AuditEntriesAsync() => Audit.AllAsync();

        public async Task SetCorrectionAttemptsAsync(int count)
        {
            var ledger = await Scope.GetLedger();
            for (var i = 0; i < count; i++)
                ledger.RecordCorrectionAttempt();
        }

        private static BudgetLimits Limits() => new(25m, TimeSpan.FromHours(4), 10, 3);

        private static WorkTask NewVerifyingTask()
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
            new[] { BackendId }.Select(id =>
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

        private static RepositoryProfile Profile(Guid id, string name, bool withCommands = true)
        {
            var command = withCommands ? "dotnet test" : null;
            var profile = new RepositoryProfile(
                name, $"https://dev.azure.com/kameleon/Platform/_git/{name}", "main",
                "test", "U-owner", RepositoryAccess.Write,
                new BuildCommands(
                    withCommands ? "dotnet restore" : null,
                    withCommands ? "dotnet build" : null,
                    command, null, ".", TimeSpan.FromMinutes(5)),
                ["CLAUDE.md"]);
            profile.RecordInspection("abc", DateTimeOffset.UtcNow);
            typeof(RepositoryProfile).GetProperty(nameof(RepositoryProfile.Id))!.SetValue(profile, id);
            return profile;
        }
    }

    private sealed class FakeCheckExecutor : ISandboxExecutor
    {
        private readonly Dictionary<string, bool> _results = [];

        public void Returns(IReadOnlyList<CheckResult> results)
        {
            _results.Clear();
            foreach (var result in results)
                _results[result.Command] = result.Passed;
        }

        public Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct)
        {
            var command = request.Command.Last();
            var passed = _results.TryGetValue(command, out var scripted) ? scripted : true;
            var output = passed ? "Passed!" : "Failed! - Failed: 1, Passed: 0";
            return Task.FromResult(new SandboxExecutionResult(
                passed ? 0 : 1, output, string.Empty, TimeSpan.FromSeconds(1), false));
        }
    }

    private sealed class FakeGitWorkspaces : IGitWorkspace
    {
        private bool _emptyDiff;

        public void ReturnsEmptyDiff() => _emptyDiff = true;

        public Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct) =>
            Task.FromResult(_emptyDiff
                ? new GitDiff("abc", "abc", [], string.Empty)
                : new GitDiff("abc", "def", [new ChangedFile("src/Orders.cs", "Modified")], "diff --git a/src/Orders.cs"));

        public Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DisposeAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkspaceStore(Guid repositoryId) : IWorkspaceStore
    {
        public Task AddAsync(Workspace workspace, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Workspace?> GetByIdAsync(string workspaceId, CancellationToken ct) =>
            Task.FromResult<Workspace?>(null);

        public Task RemoveAsync(Workspace workspace, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Workspace>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Workspace>>(
                [new Workspace(taskId, repositoryId, "/tmp/ws", "abc", "main", "orchestrator/w", "git.backend.credential", DateTimeOffset.UtcNow)]);
    }

    private sealed class FakeDomainSource : IDomainDataSource
    {
        public Task<DomainCheckResult> ExecuteNamedQueryAsync(DomainQueryRequest request, CancellationToken ct) =>
            Task.FromResult(new DomainCheckResult(
                request.QueryName, request.Environment, request.Company, true, [], $"{request.QueryName} passed"));
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        private readonly List<(ArtifactDescriptor Descriptor, string Content, string Id)> _stored = [];

        public Task<string> PutAsync(ArtifactDescriptor descriptor, Stream content, CancellationToken ct)
        {
            using var reader = new StreamReader(content);
            var id = Guid.CreateVersion7().ToString("N");
            _stored.Add((descriptor, reader.ReadToEnd(), id));
            return Task.FromResult(id);
        }

        public Task<Stream> OpenAsync(string artifactId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TaskEvidence> LatestEvidenceAsync(Guid taskId)
        {
            var latest = _stored.Last(s => s.Descriptor.TaskId == taskId && s.Descriptor.Type == "task-evidence");
            return Task.FromResult(
                System.Text.Json.JsonSerializer.Deserialize<TaskEvidence>(latest.Content)!);
        }
    }

    private sealed class FakeAgentRunStore : IAgentRunStore
    {
        private readonly List<AgentRun> _runs = [];

        public void Seed(AgentRun run) => _runs.Add(run);

        public Task AddAsync(AgentRun run, CancellationToken ct)
        {
            _runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentRun>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentRun>>(_runs.Where(r => r.TaskId == taskId).ToList());
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        private readonly List<AuditEntry> _entries = [];

        public Task RecordAsync(AuditEntry entry, CancellationToken ct)
        {
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> AllAsync() =>
            Task.FromResult<IReadOnlyList<AuditEntry>>(_entries.ToList());
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
