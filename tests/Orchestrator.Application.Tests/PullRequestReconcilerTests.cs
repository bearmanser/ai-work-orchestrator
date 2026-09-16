using Orchestrator.Application.Ports;
using Orchestrator.Application.PullRequests;
using Orchestrator.Domain.PullRequests;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class PullRequestReconcilerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid RepositoryId = Guid.CreateVersion7();
    private const string OrchestratorIdentity = "U-orchestrator";
    private static readonly string[] Reviewers = ["U-magnus", "U-peer"];
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reconcile_NewCommentSinceLastPass_IsReturnedAsNewFeedback()
    {
        var (reconciler, host) = Create(partition => partition
            .WithTracking(prId: 1, head: "abc")
            .WithDetail(prId: 1, DetailWithThreads(Thread(7, "active", "please rename the field"))));

        var result = await reconciler.ReconcileAsync(TaskId, default);

        result.NewFeedback.Single().Text.ShouldContain("please rename");
        _ = host;
    }

    [Fact]
    public async Task Reconcile_SameCommentTwice_IsNotReturnedAgain()
    {
        var (reconciler, _) = Create(partition => partition
            .WithTracking(prId: 1, head: "abc")
            .WithDetail(prId: 1, DetailWithThreads(Thread(7, "active", "please rename the field"))));

        await reconciler.ReconcileAsync(TaskId, default);
        (await reconciler.ReconcileAsync(TaskId, default)).NewFeedback.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_HeadMovedSinceApproval_ReportsHeadMoved()
    {
        var (reconciler, host) = Create(partition => partition
            .WithTracking(prId: 1, head: "abc")
            .WithDetail(prId: 1, Detail(head: "abc")));

        await reconciler.ReconcileAsync(TaskId, default);
        host.Returns(prId: 1, Detail(head: "def"));

        (await reconciler.ReconcileAsync(TaskId, default)).HeadMoved.ShouldBeTrue();
    }

    [Fact]
    public async Task Reconcile_TwoRequiredPrsOneApproved_AllRequiredApprovedIsFalse()
    {
        var (reconciler, _) = Create(partition => partition
            .WithTracking(prId: 1, head: "abc")
            .WithTracking(prId: 2, head: "abc")
            .WithDetail(prId: 1, ApprovedDetail())
            .WithDetail(prId: 2, NotApprovedDetail()));

        (await reconciler.ReconcileAsync(TaskId, default)).AllRequiredApproved.ShouldBeFalse();
    }

    [Fact]
    public async Task Reconcile_TwoRequiredPrsBothApproved_AllRequiredApprovedIsTrue()
    {
        var (reconciler, _) = Create(partition => partition
            .WithTracking(prId: 1, head: "abc")
            .WithTracking(prId: 2, head: "abc")
            .WithDetail(prId: 1, ApprovedDetail())
            .WithDetail(prId: 2, ApprovedDetail()));

        (await reconciler.ReconcileAsync(TaskId, default)).AllRequiredApproved.ShouldBeTrue();
    }

    [Fact]
    public async Task Reconcile_CommentAuthoredByTheOrchestrator_IsNotTreatedAsFeedback()
    {
        var (reconciler, _) = Create(partition => partition
            .WithTracking(prId: 1, head: "abc")
            .WithDetail(prId: 1, DetailWithThreads(
                ThreadBy(OrchestratorIdentity, 7, "active", "I have pushed a fix"))));

        (await reconciler.ReconcileAsync(TaskId, default)).NewFeedback.ShouldBeEmpty();
    }

    private static (PullRequestReconciler Reconciler, FakePullRequestHost Host) Create(
        Func<ScenarioBuilder, ScenarioBuilder> build)
    {
        var builder = build(new ScenarioBuilder());
        var store = new InMemoryPullRequestTrackingStore();
        foreach (var tracking in builder.Trackings)
        {
            store.UpsertAsync(tracking, default).GetAwaiter().GetResult();
        }

        var host = new FakePullRequestHost(builder.Details);
        var reconciler = new PullRequestReconciler(
            host,
            store,
            new FixedTimeProvider(Now),
            Reviewers,
            OrchestratorIdentity);
        return (reconciler, host);
    }

    private static PullRequestDetail Detail(string head) =>
        new(
            new PullRequestRef(1, "https://dev.azure.com/o/p/_git/r/pullRequests/1", "orchestrator/x", "main", head),
            PullRequestStatus.Active,
            [Vote("U-magnus", 10, true)],
            [],
            [new PolicyEvaluation("policy-1", "approved", null)]);

    private static PullRequestDetail ApprovedDetail() =>
        new(
            new PullRequestRef(1, "https://dev.azure.com/o/p/_git/r/pullRequests/1", "orchestrator/x", "main", "abc"),
            PullRequestStatus.Active,
            [Vote("U-magnus", 10, true)],
            [],
            [new PolicyEvaluation("policy-1", "approved", null)]);

    private static PullRequestDetail NotApprovedDetail() =>
        new(
            new PullRequestRef(2, "https://dev.azure.com/o/p/_git/r/pullRequests/2", "orchestrator/y", "main", "abc"),
            PullRequestStatus.Active,
            [Vote("U-magnus", 10, true)],
            [Thread(8, "active", "please address this")],
            [new PolicyEvaluation("policy-1", "approved", null)]);

    private static PullRequestDetail DetailWithThreads(params CommentThread[] threads) =>
        new(
            new PullRequestRef(1, "https://dev.azure.com/o/p/_git/r/pullRequests/1", "orchestrator/x", "main", "abc"),
            PullRequestStatus.Active,
            [Vote("U-magnus", 10, true)],
            threads,
            [new PolicyEvaluation("policy-1", "approved", null)]);

    private static ReviewerVote Vote(string reviewerId, int vote, bool required) =>
        new(reviewerId, reviewerId, vote, required);

    private static CommentThread Thread(int threadId, string status, string text) =>
        new(threadId, status, [new Comment(text, "U-magnus")], null);

    private static CommentThread ThreadBy(string authorId, int threadId, string status, string text) =>
        new(threadId, status, [new Comment(text, authorId)], null);

    private sealed class ScenarioBuilder
    {
        public List<PullRequestTracking> Trackings { get; } = [];
        public Dictionary<int, PullRequestDetail> Details { get; } = [];

        public ScenarioBuilder WithTracking(int prId, string head)
        {
            Trackings.Add(new PullRequestTracking(
                TaskId,
                RepositoryId,
                prId,
                $"https://dev.azure.com/o/p/_git/r/pullRequests/{prId}",
                head,
                isRequired: true,
                Now));
            return this;
        }

        public ScenarioBuilder WithDetail(int prId, PullRequestDetail detail)
        {
            Details[prId] = detail;
            return this;
        }
    }

    private sealed class FakePullRequestHost(Dictionary<int, PullRequestDetail> details) : IPullRequestHost
    {
        public void Returns(int prId, PullRequestDetail detail) => details[prId] = detail;

        public Task<PullRequestRef> CreateAsync(CreatePullRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PullRequestRef?> FindOpenBySourceBranchAsync(
            string organization,
            string project,
            string repositoryName,
            string sourceBranch,
            CancellationToken ct) =>
            Task.FromResult<PullRequestRef?>(null);

        public Task<PullRequestDetail> GetAsync(int pullRequestId, CancellationToken ct) =>
            Task.FromResult(details[pullRequestId]);

        public Task ReplyToThreadAsync(int pullRequestId, int threadId, string text, CancellationToken ct) =>
            Task.CompletedTask;

        public Task UpdateDescriptionAsync(int pullRequestId, string description, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
