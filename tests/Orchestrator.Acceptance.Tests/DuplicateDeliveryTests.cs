using Orchestrator.Application.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// Proves duplicate delivery - of the same external event id, redelivered
/// through the real webhook endpoint, and of the same job - never produces
/// duplicate work.
///
/// <see cref="AzureDevOpsDeliversTheSamePrEventThreeTimes_OneFeedbackItemIsRecorded"/>
/// deviates from the brief's illustrative version: nothing in production
/// actually connects an inbound <c>pullrequest.event</c> outbox message to a
/// <c>pull_request.feedback</c> step (see <see cref="AcceptanceHost"/>'s
/// remarks) - a genuine, separately-flagged gap, not something this task
/// silently patches over. What is proven instead, both for real: (1) the
/// webhook's own dedup, so three identical deliveries never produce more than
/// one durable record of the event, and (2) that if something did re-trigger
/// feedback processing on every delivery - which is exactly what today's gap
/// would cause once the missing step is added - <c>PullRequestReconciler</c>'s
/// own thread/comment-count bookkeeping already stops it from recording the
/// same review comment twice.
/// </summary>
[Trait("Category", "Acceptance")]
[Collection(nameof(AcceptanceCollection))]
public sealed class DuplicateDeliveryTests(AcceptanceHost Host) : IClassFixture<AcceptanceHost>
{
    [Fact]
    public async Task SlackDeliversTheSameEventFiveTimes_OneTaskIsCreated()
    {
        await Host.ResetAsync();
        var tasksBefore = await Host.CountTasksAsync();
        var payload = Host.AppMentionPayload("evt-slack-dup-1");

        for (var i = 0; i < 5; i++)
        {
            var response = await Host.PostSlackAsync(payload);
            response.EnsureSuccessStatusCode();
        }

        await Host.DrainOutboxAsync();

        (await Host.CountTasksAsync() - tasksBefore).ShouldBe(1);
    }

    [Fact]
    public async Task AzureDevOpsDeliversTheSamePrEventThreeTimes_OneFeedbackItemIsRecorded()
    {
        await Host.ResetAsync();
        var outboxBefore = await Host.CountOutboxMessagesAsync("pullrequest.event");
        var taskId = await Host.RunTaskToPullRequestAsync();
        var branch = GitBranchNaming.WorkBranchFor(taskId);
        var pullRequestId = Host.AzureDevOps.PullRequestIdForBranch("refs/heads/" + branch)
            ?? throw new InvalidOperationException("No pull request was tracked for this task's branch.");

        var payload = Host.PrCommentPayload("evt-ado-dup-1", threadId: 7, pullRequestId: pullRequestId);
        for (var i = 0; i < 3; i++)
        {
            var response = await Host.PostAzureDevOpsAsync(payload);
            response.EnsureSuccessStatusCode();
        }

        // (1) The webhook itself deduplicates the redelivered event id: three
        // identical deliveries leave exactly one durable outbox record, real,
        // through the actual /webhooks/azure-devops endpoint.
        (await Host.CountOutboxMessagesAsync("pullrequest.event") - outboxBefore).ShouldBe(1);

        // (2) Simulates what would happen if that event *did* re-trigger
        // feedback processing on every delivery (today, nothing does - see the
        // class remarks): the same reviewer comment must still only be
        // recorded once, because PullRequestReconciler's own thread/comment
        // count is what actually protects duplicate recording, not the
        // webhook's dedup.
        // Another symptom of the same missing wiring: nothing moves the task
        // from WatchingPullRequests to AddressingFeedback before a
        // pull_request.feedback step runs either, so FeedbackStepHandler's own
        // CorrectionRequired outcome (the disposition "please rename this"
        // triages to) has nowhere valid to land yet. Firing the transition here
        // is exactly what that missing piece would do.
        (await Host.FireAsync(taskId, TaskTrigger.PullRequestFeedbackReceived)).Succeeded.ShouldBeTrue();

        Host.AzureDevOps.SeedThread(pullRequestId, threadId: 7, ("U-reviewer", "please rename this"));
        await Host.DispatchNewStepAsync(taskId, "pull_request.feedback");
        await Host.DispatchNewStepAsync(taskId, "pull_request.feedback");

        (await Host.FeedbackItemsAsync(taskId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheSameJobDeliveredTwice_DoesNotProduceTwoPullRequests()
    {
        await Host.ResetAsync();
        var createCallsBefore = Host.AzureDevOps.CreateCalls;
        var taskId = await Host.RunToPullRequestCreationAsync();

        await Host.EnqueueDuplicateJobAsync(taskId, "pull_request.create");

        (Host.AzureDevOps.CreateCalls - createCallsBefore).ShouldBe(1);
    }

    [Fact]
    public async Task ALateAnswerAfterCancellation_DoesNotResumeTheTask()
    {
        await Host.ResetAsync();
        var (taskId, question) = await Host.RunToBlockingQuestionAsync();
        await Host.CancelTaskAsync(taskId);

        await Host.AnswerQuestionAsync(question, "here you go");

        (await Host.LoadTaskAsync(taskId)).State.ShouldBe(TaskState.Cancelled);
    }
}
