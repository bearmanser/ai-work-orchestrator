using Orchestrator.Application.Gateway;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.PullRequests;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class FeedbackStepHandlerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid BackendId = Guid.CreateVersion7();

    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", "magnus-ado-id", "magnus@example.com", true);

    [Fact]
    public async Task Handle_QuestionFeedback_RepliesInThreadAndReturnsFeedbackAnswered()
    {
        var harness = new Harness();
        harness.Host.SeedThread(4711, 7, "active", [("U-reviewer", "why did you use a repository here?")]);
        harness.SeedTracking(4711);
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.FeedbackAnswered);
        harness.Host.ThreadReplies.ShouldContain(r => r.PullRequestId == 4711 && r.ThreadId == 7);
    }

    [Fact]
    public async Task Handle_RenameFeedback_RecordsACorrectionAttempt()
    {
        var harness = new Harness();
        harness.Host.SeedThread(4711, 7, "active", [("U-reviewer", "please rename PickedOrderDto to PickedOrderResponse")]);
        harness.SeedTracking(4711);
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.CorrectionRequired);
        (await harness.Scope.GetLedger()).CorrectionAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_NewBehaviourFeedback_ReturnsMaterialScopeChange()
    {
        var harness = new Harness();
        harness.Host.SeedThread(4711, 7, "active", [("U-reviewer", "while you are here, also add a DELETE endpoint")]);
        harness.SeedTracking(4711);
        var outcome = await harness.Handler.HandleAsync(Context(TaskId), default);

        outcome.Trigger.ShouldBe(TaskTrigger.MaterialScopeChange);
    }

    private static StepContext Context(Guid taskId) =>
        new(taskId, Guid.CreateVersion7(), "pull_request.feedback", null);

    private sealed class Harness
    {
        public FakePullRequestHost Host { get; } = new();
        public FakeTrackingStore Tracking { get; } = new();
        public FakeToolGateway Gateway { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();
        public FakeScopeStore Scope { get; }

        private readonly InMemoryPersonStore _people = new();
        private readonly InMemoryOwnershipStore _ownership = new();
        private readonly FakeConfirmationStore _confirmations = new();

        public Harness()
        {
            _people.AddAsync(Magnus, default).GetAwaiter().GetResult();
            _ownership.AddAsync(new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository,
                BackendId.ToString(), Magnus.Id, CanConfirmPlans: true, CanApproveRestrictedActions: true),
                default).GetAwaiter().GetResult();
            Scope = new FakeScopeStore(NewAddressingTask(), Envelope(), new BudgetLedger(TaskId, Limits()));
            SeedPlan();
        }

        public FeedbackStepHandler Handler => new(
            Scope,
            _confirmations,
            Host,
            Tracking,
            Gateway,
            Audit,
            new OwnershipDirectory(_people, _ownership),
            TimeProvider.System,
            new TaskCompletionOptions
            {
                OrchestratorIdentityId = "U-orchestrator",
                FallbackChannelId = "C1",
            });

        public void SeedTracking(int pullRequestId)
        {
            var tracking = new PullRequestTracking(
                TaskId, BackendId, pullRequestId,
                $"https://dev.azure.com/x/_git/backend/pullrequest/{pullRequestId}",
                "abc", true, DateTimeOffset.UtcNow);
            Tracking.Seed(tracking);
            Host.SeedDetailHead(pullRequestId, "abc");
        }

        private void SeedPlan()
        {
            var content = new PlanContent(
                "Add ordering endpoint", ["api"], [], "Add a controller plus tests",
                [new PlannedRepositoryChange(BackendId, "backend", ChangeIntent.Change, "abc")],
                ["dotnet test"], [], [],
                Limits(), ["replay"], []);
            var revision = new PlanRevision(TaskId, 1, content);
            revision.Confirm(Magnus.Id, DateTimeOffset.UtcNow);
            _confirmations.SeedRevision(revision);
        }

        private static BudgetLimits Limits() => new(25m, TimeSpan.FromHours(4), 10, 3);

        private static WorkTask NewAddressingTask()
        {
            var task = new WorkTask("objective", Magnus.Id.ToString(), TaskSource.Slack,
                "slack:C1:1737000000.000100", DateTimeOffset.UtcNow);
            typeof(WorkTask).GetProperty(nameof(WorkTask.Id))!.SetValue(task, TaskId);
            task.Fire(TaskTrigger.PlanningStarted, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.PlanPosted, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.PlanConfirmed, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.WorkDispatched, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.ImplementationFinished, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.PullRequestOpened, DateTimeOffset.UtcNow);
            task.Fire(TaskTrigger.PullRequestFeedbackReceived, DateTimeOffset.UtcNow);
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
    }

    private sealed class FakePullRequestHost : IPullRequestHost
    {
        private readonly Dictionary<int, PullRequestDetail> _details = [];
        public List<(int PullRequestId, int ThreadId, string Text)> ThreadReplies { get; } = [];

        public void SeedThread(int pullRequestId, int threadId, string status, IReadOnlyList<(string Author, string Text)> comments)
        {
            if (!_details.TryGetValue(pullRequestId, out var current))
            {
                current = new PullRequestDetail(
                    new PullRequestRef(pullRequestId,
                        $"https://dev.azure.com/x/_git/backend/pullrequest/{pullRequestId}",
                        $"orchestrator/{TaskId}", "main", "abc"),
                    PullRequestStatus.Active, [], [], [new PolicyEvaluation("p1", "approved", null)]);
            }

            var threads = current.Threads
                .Where(t => t.ThreadId != threadId)
                .Append(new CommentThread(threadId, status,
                    comments.Select(c => new Comment(c.Text, c.Author)).ToList(), null))
                .ToList();
            _details[pullRequestId] = current with { Threads = threads };
        }

        public void SeedDetailHead(int pullRequestId, string head)
        {
            if (_details.TryGetValue(pullRequestId, out var current))
                _details[pullRequestId] = current with
                {
                    Ref = current.Ref with { LastMergeSourceCommit = head },
                };
        }

        public Task<PullRequestRef> CreateAsync(CreatePullRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PullRequestRef?> FindOpenBySourceBranchAsync(
            string organization, string project, string repositoryName, string sourceBranch,
            CancellationToken ct) => Task.FromResult<PullRequestRef?>(null);

        public Task<PullRequestDetail> GetAsync(int pullRequestId, CancellationToken ct) =>
            Task.FromResult(_details.TryGetValue(pullRequestId, out var detail)
                ? detail
                : new PullRequestDetail(
                    new PullRequestRef(pullRequestId,
                        $"https://dev.azure.com/x/_git/backend/pullrequest/{pullRequestId}",
                        $"orchestrator/{TaskId}", "main", "abc"),
                    PullRequestStatus.Active, [], [],
                    [new PolicyEvaluation("p1", "approved", null)]));

        public Task ReplyToThreadAsync(int pullRequestId, int threadId, string text, CancellationToken ct)
        {
            ThreadReplies.Add((pullRequestId, threadId, text));
            return Task.CompletedTask;
        }

        public Task UpdateDescriptionAsync(int pullRequestId, string description, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeTrackingStore : IPullRequestTrackingStore
    {
        private readonly Dictionary<Guid, PullRequestTracking> _trackings = [];

        public void Seed(PullRequestTracking tracking) => _trackings[tracking.Id] = tracking;

        public Task<IReadOnlyList<PullRequestTracking>> ListByTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PullRequestTracking>>(
                _trackings.Values.Where(t => t.TaskId == taskId).ToList());

        public Task UpsertAsync(PullRequestTracking tracking, CancellationToken ct)
        {
            _trackings[tracking.Id] = tracking;
            return Task.CompletedTask;
        }

        public Task<PullRequestTracking?> FindByPullRequestIdAsync(int pullRequestId, CancellationToken ct) =>
            Task.FromResult(_trackings.Values.FirstOrDefault(t => t.PullRequestId == pullRequestId));
    }

    private sealed class FakeToolGateway : IToolGateway
    {
        public List<ToolCall> Calls { get; } = [];

        public async Task<ToolResult<TResult>> InvokeAsync<TResult>(
            ToolCall call, Func<CancellationToken, Task<TResult>> invoke, CancellationToken ct)
        {
            Calls.Add(call);
            var value = await invoke(ct);
            return new ToolResult<TResult>(true, value, new PolicyDecision.Permitted("test"));
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
}
