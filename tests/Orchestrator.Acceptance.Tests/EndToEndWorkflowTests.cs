using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// Proves the pipeline the whole plan was building actually runs end to end
/// through the real host, driven only by <see cref="AcceptanceHost.DrainAsync"/> -
/// with no test-side step seeding - now that <c>NextStepScheduler</c> (Task 50)
/// chains the next Step after a trigger fires. Before that fix, nothing in
/// production created a Step after the first ("planning") one, so every one of
/// these scenarios would have stalled at <see cref="TaskState.Ready"/> or
/// <see cref="TaskState.Verifying"/> forever (see task-49-report.md and
/// <see cref="AcceptanceHost"/>'s own remarks, which recorded this as the
/// structural gap task 49's suite had to work around).
///
/// <b>What is deliberately NOT here, and why:</b> the brief's illustrative draft
/// sketched a considerably larger <c>AcceptanceHost</c> surface than exists in
/// this codebase - per-agent-stage invocation counters, an admin sign-in
/// endpoint returning a typed <c>TaskDetail</c>, a Vault write log, a P1 policy
/// denial path, and a dual-provider "bounded assignment" contract test. None of
/// that infrastructure exists yet (there is no <c>IVaultStore</c> write capture,
/// no <c>/api/admin/tasks/{id}</c> sign-in flow wired to this host, and
/// <c>AgentRouter</c> exposes no per-stage invocation count). Building all of
/// it from scratch, unverified, in an environment where Docker is unavailable
/// (see the task report) would not be a genuine proof of anything - it is
/// flagged here rather than faked. The scenarios below are the subset that
/// exercises real, already-wired production code end to end.
/// </summary>
[Trait("Category", "Acceptance")]
[Collection(nameof(AcceptanceCollection))]
public sealed class EndToEndWorkflowTests(AcceptanceHost Host) : IClassFixture<AcceptanceHost>
{
    [Fact]
    public async Task SingleRepositoryTask_PlanConfirmationAloneDispatchesImplementationAndVerification()
    {
        await Host.ResetAsync();

        var taskId = await Host.SubmitSlackTaskAsync("add a GET endpoint");
        await Host.DrainAsync();
        (await Host.LoadTaskAsync(taskId)).State.ShouldBe(TaskState.WaitingForPlanConfirmation);

        await Host.ConfirmPlanAsync(taskId);
        await Host.DrainAsync();

        // Confirming the plan is the only thing this test does by hand from here
        // on: NextStepScheduler is what turns that single action into an actual
        // implementation attempt and a verification pass, with no step seeded by
        // the test. Ready is transient (WorkDispatched is fired by the scheduler
        // itself, since nothing else in production fires it), so the task must
        // have left both WaitingForPlanConfirmation and Ready.
        var task = await Host.LoadTaskAsync(taskId);
        task.State.ShouldNotBe(TaskState.WaitingForPlanConfirmation);
        task.State.ShouldNotBe(TaskState.Ready);

        var dump = await Host.DumpStepsAsync(taskId);
        dump.ShouldContain("coding_agent.execute: Succeeded");
        dump.ShouldContain("verification");
    }

    [Fact]
    public async Task ImplementationCannotStartWithoutConfirmation()
    {
        await Host.ResetAsync();

        var taskId = await Host.SubmitSlackTaskAsync("add a GET endpoint");
        await Host.DrainAsync();
        await Host.DrainAsync(); // deliberately drain twice without confirming

        (await Host.LoadTaskAsync(taskId)).State.ShouldBe(TaskState.WaitingForPlanConfirmation);
        (await Host.DumpStepsAsync(taskId)).ShouldNotContain("coding_agent.execute");
    }

    [Fact]
    public async Task PullRequestFeedbackWebhook_WakesTheTaskAndTriagesWithoutOpeningASecondPullRequest()
    {
        await Host.ResetAsync();

        // RunTaskToPullRequestAsync (Task 49's helper) drives a task, seeded
        // straight into Ready/Running, through a real coding_agent.execute step
        // and a real pull_request.create step, landing it in WatchingPullRequests
        // with one tracked pull request - this part predates Task 50.
        var taskId = await Host.RunTaskToPullRequestAsync();
        (await Host.LoadTaskAsync(taskId)).State.ShouldBe(TaskState.WatchingPullRequests);

        var pullRequestId = (await Host.TrackedPullRequestsAsync(taskId))[0].PullRequestId;
        var createCallsBefore = Host.AzureDevOps.CreateCalls;

        // This is the part Task 50 actually wires: AzureDevOpsWebhookEndpoint has
        // enqueued a "pullrequest.event" outbox message for every comment event
        // since Task 25/48, but nothing consumed that message type before this
        // task (see PullRequestFeedbackRouter's remarks, and DuplicateDeliveryTests'
        // note on the same gap). Posting a real comment webhook must now wake the
        // task into AddressingFeedback and run the real "pull_request.feedback" step.
        var response = await Host.PostAzureDevOpsAsync(
            Host.PrCommentPayload("evt-e2e-feedback-1", threadId: 1, pullRequestId: pullRequestId));
        response.EnsureSuccessStatusCode();
        await Host.DrainAsync();

        (await Host.DumpStepsAsync(taskId)).ShouldContain("pull_request.feedback");
        // The in-scope feedback triage answers in the review thread rather than
        // opening a new pull request.
        Host.AzureDevOps.CreateCalls.ShouldBe(createCallsBefore);
        (await Host.TrackedPullRequestsAsync(taskId)).Count.ShouldBe(1);
    }
}
