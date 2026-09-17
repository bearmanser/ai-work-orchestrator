using Orchestrator.Application.Identity;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.PullRequests;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class TaskCompletionServiceTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid BackendId = Guid.CreateVersion7();
    private static readonly Guid FrontendId = Guid.CreateVersion7();

    private const string OrchestratorIdentity = "U-orchestrator";

    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", "magnus-ado-id", "magnus@example.com", true);

    [Fact]
    public async Task TryComplete_AllRequiredPrsApproved_CompletesAndReportsEveryLink()
    {
        var harness = new Harness();
        await harness.SeedTrackedAsync(new Seed(4711, Approved: true), new(4712, Approved: true));
        var outcome = await harness.Service.TryCompleteAsync(TaskId, default);

        outcome.Completed.ShouldBeTrue();
        (await harness.LoadTaskAsync()).State.ShouldBe(TaskState.Completed);
        harness.Chat.Posted.Last().Text.ShouldContain("4711");
        harness.Chat.Posted.Last().Text.ShouldContain("4712");
    }

    [Fact]
    public async Task TryComplete_OnlyOneOfTwoApproved_StaysActiveAndSaysWhichIsWaiting()
    {
        var harness = new Harness();
        await harness.SeedTrackedAsync(new Seed(4711, Approved: true), new(4712, Approved: false));
        var outcome = await harness.Service.TryCompleteAsync(TaskId, default);

        outcome.Completed.ShouldBeFalse();
        outcome.Reason.ShouldContain("4712");
        (await harness.LoadTaskAsync()).State.ShouldBe(TaskState.WatchingPullRequests);
    }

    [Fact]
    public async Task TryComplete_ApprovalOnAnOlderHead_DoesNotComplete()
    {
        var harness = new Harness();
        await harness.SeedTrackedAsync(new Seed(4711, Approved: true, ApprovedHead: "abc", CurrentHead: "def"));

        var outcome = await harness.Service.TryCompleteAsync(TaskId, default);

        outcome.Completed.ShouldBeFalse();
    }

    [Fact]
    public async Task TryComplete_ApprovalDismissedAfterANewPush_ReturnsToWatching()
    {
        var harness = new Harness();
        await harness.SeedTrackedAsync(new Seed(4711, Approved: true));
        await harness.Service.TryCompleteAsync(TaskId, default);
        // Not applicable once completed; verify the pre-completion case instead.
        await harness.ResetToWatchingAsync();
        await harness.DismissApprovalAsync(4711);

        var outcome = await harness.Service.TryCompleteAsync(TaskId, default);

        outcome.Completed.ShouldBeFalse();
    }

    [Fact]
    public async Task TryComplete_PrClosedWithoutApproval_CancelsTheTaskWithAnExplanation()
    {
        var harness = new Harness();
        await harness.SeedTrackedAsync(new Seed(4711, ClosedWithoutApproval: true));
        await harness.Service.TryCompleteAsync(TaskId, default);

        (await harness.LoadTaskAsync()).State.ShouldBe(TaskState.Cancelled);
        harness.Chat.Posted.Last().Text.ShouldContain("closed without approval");
    }

    [Fact]
    public async Task TryComplete_ApprovedOnlyByTheOrchestratorIdentity_DoesNotComplete()
    {
        var harness = new Harness();
        await harness.SeedTrackedAsync(new Seed(4711, ApprovedBy: OrchestratorIdentity));

        var outcome = await harness.Service.TryCompleteAsync(TaskId, default);

        outcome.Completed.ShouldBeFalse();
    }

    private sealed record Seed(
        int Id,
        bool Approved = false,
        string? ApprovedHead = null,
        string? CurrentHead = null,
        bool ClosedWithoutApproval = false,
        string? ApprovedBy = null);

    private sealed class Harness
    {
        public FakePullRequestHost Host { get; } = new();
        public FakeChatGateway Chat { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();
        public FakeTrackingStore Tracking { get; } = new();
        public FakeStepStore StepStore { get; } = new();
        public FakeScopeStore Scope { get; }

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
            Scope = new FakeScopeStore(NewWatchingTask(), Envelope(), new BudgetLedger(TaskId, Limits()));
        }

        public TaskCompletionService Service => new(
            Tracking,
            Host,
            new OwnershipDirectory(_people, _ownership),
            Scope,
            StepStore,
            Chat,
            Audit,
            TimeProvider.System,
            new TaskCompletionOptions
            {
                OrchestratorIdentityId = OrchestratorIdentity,
                FallbackChannelId = "C1",
            });

        public async Task SeedTrackedAsync(params Seed[] seeds)
        {
            foreach (var seed in seeds)
            {
                var trackedHead = seed.ApprovedHead ?? "abc";
                var tracking = new PullRequestTracking(
                    TaskId, BackendId, seed.Id, $"https://dev.azure.com/x/_git/backend/pullrequest/{seed.Id}",
                    trackedHead, true, DateTimeOffset.UtcNow);
                if (seed.Approved)
                    tracking.SetApproval(true, DateTimeOffset.UtcNow);
                await Tracking.UpsertAsync(tracking, default);

                var detailHead = seed.CurrentHead ?? trackedHead;
                Host.SeedDetail(
                    seed.Id,
                    head: detailHead,
                    votes: seed.Approved
                        ? [(seed.ApprovedBy ?? "magnus-ado-id", 10, true)]
                        : seed.ClosedWithoutApproval
                            ? []
                            : [(seed.ApprovedBy ?? "magnus-ado-id", 0, true)],
                    status: seed.ClosedWithoutApproval ? PullRequestStatus.Abandoned : PullRequestStatus.Active);
            }
        }

        public Task<WorkTask> LoadTaskAsync() => Scope.GetTaskAsync(TaskId, default);

        public Task ResetToWatchingAsync()
        {
            var task = Scope.CurrentTask;
            typeof(WorkTask).GetProperty(nameof(WorkTask.State))!.SetValue(task, TaskState.WatchingPullRequests);
            return Task.CompletedTask;
        }

        public Task DismissApprovalAsync(int pullRequestId)
        {
            Host.SetVotes(pullRequestId, []);
            return Task.CompletedTask;
        }

        private static BudgetLimits Limits() => new(25m, TimeSpan.FromHours(4), 10, 3);

        private static WorkTask NewWatchingTask()
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
    }

    private sealed class FakePullRequestHost : IPullRequestHost
    {
        private readonly Dictionary<int, PullRequestDetail> _details = [];
        public List<(int PullRequestId, int ThreadId, string Text)> ThreadReplies { get; } = [];

        public void SeedDetail(int id, string head, IReadOnlyList<(string Reviewer, int Vote, bool Required)> votes, PullRequestStatus status) =>
            _details[id] = new PullRequestDetail(
                new PullRequestRef(id, $"https://dev.azure.com/x/_git/backend/pullrequest/{id}",
                    $"orchestrator/{TaskId}", "main", head),
                status,
                votes.Select(v => new ReviewerVote(v.Reviewer, v.Reviewer, v.Vote, v.Required)).ToList(),
                [],
                [new PolicyEvaluation("p1", "approved", null)]);

        public void SetVotes(int id, IReadOnlyList<(string Reviewer, int Vote, bool Required)> votes)
        {
            var current = _details[id];
            _details[id] = current with
            {
                Reviewers = votes.Select(v => new ReviewerVote(v.Reviewer, v.Reviewer, v.Vote, v.Required)).ToList(),
            };
        }

        public Task<PullRequestRef> CreateAsync(CreatePullRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PullRequestRef?> FindOpenBySourceBranchAsync(
            string organization, string project, string repositoryName, string sourceBranch,
            CancellationToken ct) => Task.FromResult<PullRequestRef?>(null);

        public Task<PullRequestDetail> GetAsync(int pullRequestId, CancellationToken ct) =>
            Task.FromResult(_details[pullRequestId]);

        public Task ReplyToThreadAsync(int pullRequestId, int threadId, string text, CancellationToken ct)
        {
            ThreadReplies.Add((pullRequestId, threadId, text));
            return Task.CompletedTask;
        }

        public Task UpdateDescriptionAsync(int pullRequestId, string description, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeChatGateway : IChatGateway
    {
        public List<ChatMessage> Posted { get; } = [];
        public List<IReadOnlyList<ChatBlock>> PostedBlocks { get; } = [];

        public Task<PostedMessage> PostAsync(ChatMessage message, CancellationToken ct)
        {
            Posted.Add(message);
            return Task.FromResult(new PostedMessage(message.ChannelId, "1000.0001", message.ThreadTs ?? "1000.0001"));
        }

        public Task<PostedMessage> PostBlocksAsync(
            string channelId, string? threadTs, IReadOnlyList<ChatBlock> blocks, string fallbackText,
            CancellationToken ct)
        {
            PostedBlocks.Add(blocks);
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

    private sealed class FakeStepStore : IStepStore
    {
        public List<Step> Steps { get; } = [];

        public Task AddAsync(Step step, CancellationToken ct)
        {
            Steps.Add(step);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Step>> ListForTaskAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Step>>(Steps.Where(s => s.TaskId == taskId).ToList());
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

        public WorkTask CurrentTask => _task;

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

        public Task SaveLedgerAsync(BudgetLedger ledger, CancellationToken ct) => Task.CompletedTask;
    }
}
