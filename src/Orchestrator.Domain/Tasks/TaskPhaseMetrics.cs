using System.Diagnostics.Metrics;

namespace Orchestrator.Domain.Tasks;

/// <summary>
/// Records how long a task spent in a phase before <see cref="WorkTask.Fire"/> moved
/// it out of that phase, tagged by the coarse split PROJECT_PLAN.md section 13 asks
/// for: executing time versus time spent waiting on a person versus time spent
/// waiting on a pull request review.
///
/// This lives in Domain, not Infrastructure, because <see cref="WorkTask.Fire"/> is
/// the single place every state transition passes through - including in tests (and
/// any future caller) that construct a <see cref="WorkTask"/> directly with no DI
/// container wired up. A listener a caller had to remember to attach would miss
/// exactly the transitions this metric exists to catch. <see cref="System.Diagnostics.Metrics"/>
/// is part of the base class library, not one of the infrastructure concerns
/// (EF Core, Npgsql, Hangfire, HttpClient, DI) Domain is kept free of.
/// </summary>
internal static class TaskPhaseMetrics
{
    /// <summary>
    /// Shared with <c>Orchestrator.Infrastructure.Telemetry.OrchestratorMetrics</c> so
    /// every instrument - wherever it is defined - is exported under one meter name.
    /// </summary>
    public const string MeterName = "OrchestratorMetrics";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "orchestrator.task.duration", "s", "Time a task spent in a phase before transitioning out of it.");

    public static void RecordPhaseDuration(TaskState from, DateTimeOffset since, DateTimeOffset now)
    {
        var elapsedSeconds = (now - since).TotalSeconds;
        if (elapsedSeconds < 0)
            elapsedSeconds = 0;

        Duration.Record(elapsedSeconds, new KeyValuePair<string, object?>("phase", PhaseOf(from)));
    }

    private static string PhaseOf(TaskState state) => state switch
    {
        TaskState.WaitingForHuman => "waiting_for_human",
        TaskState.WaitingForPlanConfirmation => "waiting_for_human",
        TaskState.WaitingForActionApproval => "waiting_for_human",
        TaskState.Suspended => "waiting_for_human",
        TaskState.WatchingPullRequests => "waiting_for_review",
        _ => "executing",
    };
}
