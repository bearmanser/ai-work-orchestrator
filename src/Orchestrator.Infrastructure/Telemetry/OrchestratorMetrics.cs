using System.Diagnostics.Metrics;

namespace Orchestrator.Infrastructure.Telemetry;

/// <summary>
/// OpenTelemetry metrics for the orchestrator. All instruments are created once at
/// startup and published through the global <see cref="Meter"/>. Consumers should
/// configure an exporter (OTLP, Prometheus, etc.) in Program.cs.
/// </summary>
public static class OrchestratorMetrics
{
    public const string MeterName = "Orchestrator";

    private static readonly Meter _meter = new(MeterName);

    // Task completion/failure by capability
    public static readonly Counter<long> TaskCompleted =
        _meter.CreateCounter<long>("orchestrator.task.completed", "tasks", "Number of tasks completed by capability");

    public static readonly Counter<long> TaskFailed =
        _meter.CreateCounter<long>("orchestrator.task.failed", "tasks", "Number of tasks failed by capability");

    // Task duration by phase (executing vs waiting_for_human vs waiting_for_review)
    public static readonly Histogram<double> TaskDuration =
        _meter.CreateHistogram<double>("orchestrator.task.duration", "seconds", "Task duration split by phase");

    // Retries and reconciliations
    public static readonly Counter<long> StepRetries =
        _meter.CreateCounter<long>("orchestrator.step.retries", "retries", "Number of step retries");

    public static readonly Counter<long> TaskReconciliations =
        _meter.CreateCounter<long>("orchestrator.task.reconciliations", "reconciliations", "Number of task reconciliations");

    // Duplicate suppressions
    public static readonly Counter<long> DuplicateSuppressions =
        _meter.CreateCounter<long>("orchestrator.duplicates.suppressed", "suppressions", "Number of duplicate deliveries suppressed");

    // Agent cost and duration by stage
    public static readonly Histogram<double> AgentCost =
        _meter.CreateHistogram<double>("orchestrator.agent.cost", "USD", "Agent invocation cost by stage");

    public static readonly Histogram<double> AgentDuration =
        _meter.CreateHistogram<double>("orchestrator.agent.duration", "seconds", "Agent invocation duration by stage");

    // Policy denials
    public static readonly Counter<long> PolicyDenials =
        _meter.CreateCounter<long>("orchestrator.policy.denials", "denials", "Number of policy denials by action and resource");

    // Approval latency
    public static readonly Histogram<double> ApprovalLatency =
        _meter.CreateHistogram<double>("orchestrator.approval.latency", "seconds", "Time from approval request to decision");

    // Memory publications and deferrals
    public static readonly Counter<long> MemoryPublications =
        _meter.CreateCounter<long>("orchestrator.memory.publications", "publications", "Number of memory publications");

    public static readonly Counter<long> MemoryDeferrals =
        _meter.CreateCounter<long>("orchestrator.memory.deferrals", "deferrals", "Number of memory deferrals");

    // Transcript review routing
    public static readonly Counter<long> TranscriptReviewRouted =
        _meter.CreateCounter<long>("orchestrator.transcript.review.routed", "routings", "Number of transcripts routed for review");

    public static Meter Meter => _meter;
}
