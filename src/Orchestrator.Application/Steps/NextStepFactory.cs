using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Steps;

/// <summary>
/// Pure mapping from "a task just fired this trigger from this state" to the
/// capability of the step that must run next. No I/O: <see cref="NextStepScheduler"/>
/// is what actually creates and enqueues the step.
///
/// Keyed by (from-state, trigger) rather than by destination state alone, because
/// the same destination state is reached by different triggers that need different
/// treatment - most importantly <see cref="TaskState.WatchingPullRequests"/>, which
/// needs a new "pull_request.create" step the first time it is reached from
/// <see cref="TaskState.Verifying"/>, but must NOT get a second one when the
/// pull-request-creation step itself lands the task back in the same state
/// (the (WatchingPullRequests, PullRequestOpened) self-loop), or when feedback
/// handling returns to it via <see cref="TaskTrigger.FeedbackAnswered"/> (that path
/// is reconciliation-polling driven, not step driven).
/// </summary>
public static class NextStepFactory
{
    public const string Planning = "planning";
    public const string CodingAgentExecute = "coding_agent.execute";
    public const string Verification = "verification";
    public const string PullRequestCreate = "pull_request.create";
    public const string PullRequestFeedback = "pull_request.feedback";
    public const string MemoryCurate = "memory.curate";

    public static string? CapabilityFor(TaskState from, TaskTrigger trigger) => (from, trigger) switch
    {
        // A confirmed plan or an approved restricted action both land the task in
        // Ready; either way, the next thing that must run is an implementation
        // (or correction) attempt.
        (TaskState.WaitingForPlanConfirmation, TaskTrigger.PlanConfirmed) => CodingAgentExecute,
        (TaskState.WaitingForActionApproval, TaskTrigger.ActionApproved) => CodingAgentExecute,

        // An implementation attempt finished; verify it.
        (TaskState.Running, TaskTrigger.ImplementationFinished) => Verification,

        // Verification (or feedback triage) asked for another implementation pass.
        // Bounded by the budget ledger's CorrectionAttempts/EnsureWithinLimits,
        // which VerificationStepHandler and FeedbackStepHandler already enforce.
        (TaskState.Verifying, TaskTrigger.CorrectionRequired) => CodingAgentExecute,
        (TaskState.AddressingFeedback, TaskTrigger.CorrectionRequired) => CodingAgentExecute,

        // Verification passed with a non-empty diff: open the pull request(s).
        // Only from Verifying - the (WatchingPullRequests, PullRequestOpened)
        // self-loop that the creation step's own outcome causes must not
        // re-trigger a second creation step.
        (TaskState.Verifying, TaskTrigger.PullRequestOpened) => PullRequestCreate,

        // New review feedback arrived on a watched pull request: triage it.
        (TaskState.WatchingPullRequests, TaskTrigger.PullRequestFeedbackReceived) => PullRequestFeedback,

        // Everything else - WaitingForHuman, WaitingForPlanConfirmation,
        // WaitingForActionApproval, FeedbackAnswered back to WatchingPullRequests
        // (reconciliation-polling driven, not step driven), AllRequiredApprovalsVerified
        // to Completed (TaskCompletionService already creates "memory.curate" itself),
        // and every terminal/idle transition - creates nothing here.
        _ => null,
    };
}
