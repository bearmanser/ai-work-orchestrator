using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.PullRequests;

/// <summary>
/// Routes an inbound Azure DevOps pull-request-comment webhook event to the task
/// that opened the pull request, firing <see cref="TaskTrigger.PullRequestFeedbackReceived"/>
/// so the pipeline creates a "pull_request.feedback" step (via <see cref="NextStepScheduler"/>).
///
/// This is the "PR feedback event ... leading to AddressingFeedback" wiring the
/// webhook/outbox pipeline was missing: <c>AzureDevOpsWebhookEndpoint</c> already
/// enqueues a <c>pullrequest.event</c> outbox message for every comment event, but
/// nothing consumed that message type before this. Non-comment events (PR updated,
/// merged) and events for a task that is not currently watching pull requests
/// (feedback already being handled, or the task moved on) are ignored - the
/// per-thread reconciler inside <see cref="Steps.FeedbackStepHandler"/> is what
/// actually de-duplicates comments, this router only decides whether to wake a
/// task up.
/// </summary>
public sealed class PullRequestFeedbackRouter(
    IPullRequestTrackingStore tracking,
    IPlanningScopeStore scope,
    NextStepScheduler nextStep,
    IAuditWriter audit,
    TimeProvider clock)
{
    public const string CommentEventType = "ms.vss-code.git-pullrequest-comment-event";

    public async Task HandleAsync(string eventType, int pullRequestId, CancellationToken ct)
    {
        if (!string.Equals(eventType, CommentEventType, StringComparison.Ordinal))
            return;

        var tracked = await tracking.FindByPullRequestIdAsync(pullRequestId, ct);
        if (tracked is null)
            return;

        var task = await scope.GetTaskAsync(tracked.TaskId, ct);
        if (task.State != TaskState.WatchingPullRequests)
            return;

        var from = task.State;
        var now = clock.GetUtcNow();
        task.Fire(TaskTrigger.PullRequestFeedbackReceived, now);
        await nextStep.ScheduleAsync(task, from, TaskTrigger.PullRequestFeedbackReceived, ct);
        await scope.SaveTaskAsync(task, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: "orchestrator",
            TaskId: task.Id,
            Action: "pullrequest.feedback-received",
            Reason: $"Comment activity observed on pull request {pullRequestId}.",
            Outcome: "Success",
            CorrelationId: pullRequestId.ToString(),
            Details: new Dictionary<string, string> { ["pullRequestId"] = pullRequestId.ToString() }), ct);
    }
}
