using Orchestrator.Application.Gateway;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// Proves that a restart at each of the points where a task can be caught
/// mid-flight - before the outbox has dispatched, mid step, waiting for a plan
/// confirmation, waiting for a human answer, and after a lease is abandoned -
/// leaves the task exactly where it was and never re-does already-done work.
/// See <see cref="AcceptanceHost"/>'s remarks for the two production gaps
/// (no step-advancement wiring past planning, no <c>IOperationReconciler</c>)
/// this suite had to work around or fix to be able to prove these at all.
/// </summary>
[Trait("Category", "Acceptance")]
[Collection(nameof(AcceptanceCollection))]
public sealed class RecoveryTests(AcceptanceHost Host) : IClassFixture<AcceptanceHost>
{
    [Fact]
    public async Task Restart_BeforeOutboxDispatch_StillDeliversExactlyOnce()
    {
        await Host.ResetAsync();
        await Host.PauseOutboxAsync();
        var taskId = await Host.SubmitSlackTaskAsync("add a GET endpoint");
        await Host.RestartAsync();
        await Host.DrainOutboxAsync();

        (await Host.AgentInvocationsForAsync(taskId)).ShouldBe(1);
    }

    [Fact]
    public async Task Restart_DuringAnAgentRun_ResumesWithoutASecondInvocation()
    {
        await Host.ResetAsync();
        var taskId = await Host.RunToAgentInvocationAsync();
        (await Host.AgentInvocationsForAsync(taskId)).ShouldBe(1);

        await Host.RestartAsync();
        await Host.DrainJobsAsync();

        (await Host.AgentInvocationsForAsync(taskId)).ShouldBe(1);
    }

    [Fact]
    public async Task Restart_WhileWaitingForAPlanConfirmation_PreservesTheTaskAndTheRevisionHash()
    {
        await Host.ResetAsync();
        var (taskId, hash) = await Host.RunToPlanConfirmationAsync();
        await Host.RestartAsync();

        var task = await Host.LoadTaskAsync(taskId);
        task.State.ShouldBe(TaskState.WaitingForPlanConfirmation);
        (await Host.CurrentPlanHashAsync(taskId)).ShouldBe(hash);
    }

    [Fact]
    public async Task Restart_WhileWaitingForAHumanAnswer_ResumesFromTheAnswer()
    {
        await Host.ResetAsync();
        var (taskId, question) = await Host.RunToBlockingQuestionAsync();
        (await Host.LoadTaskAsync(taskId)).State.ShouldBe(TaskState.WaitingForHuman);

        await Host.RestartAsync();
        await Host.AnswerQuestionAsync(question, "yes, include cancelled orders");

        (await Host.LoadTaskAsync(taskId)).State.ShouldBe(TaskState.Planning);
    }

    [Fact]
    public async Task Crash_AfterAzureDevOpsCreatedThePr_ButBeforeTheOutcomeWasRecorded_DoesNotCreateASecondPr()
    {
        // A delta, not an absolute count: AzureDevOps.CreateCalls is a single
        // WireMock request log shared for this AcceptanceHost's whole lifetime
        // (across every test method in this class), so only the calls this test
        // itself causes must be counted.
        await Host.ResetAsync();
        var createCallsBefore = Host.AzureDevOps.CreateCalls;
        var taskId = await Host.RunToPullRequestCreationAsync(crashAfterRemoteSuccess: true);

        await Should.ThrowAsync<UnreconciledOperationException>(
            () => Host.RetryStepAsync(taskId, "pull_request.create"));

        var summary = await Host.ReconcileAsync(taskId);
        summary.FullyReconciled.ShouldBeTrue();

        await Host.RetryStepAsync(taskId, "pull_request.create");

        (Host.AzureDevOps.CreateCalls - createCallsBefore).ShouldBe(1);
        (await Host.TrackedPullRequestsAsync(taskId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredLease_IsReclaimedAndTheStepRunsOnce()
    {
        await Host.ResetAsync();
        var (_, stepId) = await Host.LeaseStepAndAbandonAsync();
        Host.Clock.Advance(TimeSpan.FromMinutes(10));
        await Host.RunLeaseReclaimAsync();
        await Host.DrainJobsAsync();

        (await Host.StepAttemptsAsync(stepId)).ShouldBe(1);
    }

    [Fact]
    public async Task UncommittedChangesInADeveloperCheckout_SurviveAWholeTask()
    {
        await Host.ResetAsync();
        var developerFile = await Host.CreateDeveloperWorkingCopyChangeAsync("work in progress");
        await Host.RunTaskToPullRequestAsync();

        (await File.ReadAllTextAsync(developerFile)).ShouldBe("work in progress");
    }
}
