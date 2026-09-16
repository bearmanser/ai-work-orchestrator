using System.Text.Json;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Messaging;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Slack;

/// <summary>Configuration for task intake defaults.</summary>
public sealed class TaskIntakeOptions
{
    public Guid[] DefaultRepositoryIds { get; set; } = [];
    public string[] DefaultAllowedProviders { get; set; } = ["codex", "claude"];
    public BudgetLimits DefaultBudget { get; set; } = new(
        MaxUsd: 25m,
        MaxWallClock: TimeSpan.FromHours(4),
        MaxAgentInvocations: 10,
        MaxCorrectionAttempts: 3);
}

public sealed record IntakeResult(Guid? TaskId, string Reply);

/// <summary>Everything intake creates for one accepted request, in one transaction.</summary>
public sealed record TaskIntakeGraph(
    WorkTask Task,
    AuthorizationEnvelope Envelope,
    BudgetLedger Ledger,
    Step PlanningStep,
    OutboxMessage Message);

/// <summary>
/// Durable intake: every aggregate the task needs is written by one store call
/// so partial intake is impossible.
/// </summary>
public interface ITaskIntakeStore
{
    Task<bool> ExistsForCorrelationAsync(string correlationId, CancellationToken ct);

    /// <summary>Creates the whole graph atomically; null when the correlation id is taken.</summary>
    Task<Guid?> TryCreateAsync(TaskIntakeGraph graph, CancellationToken ct);
}

/// <summary>
/// Turns an addressed Slack message from a known person into a task in
/// Planning, with its authorization envelope, budget, planning step, and an
/// outbox message that gets the step dispatched. Idempotent per Slack message.
/// </summary>
public sealed class TaskIntakeService(
    ITaskIntakeStore store,
    IOperationalControls operations,
    IAuditWriter audit,
    TimeProvider clock,
    TaskIntakeOptions options)
{
    public async Task<IntakeResult> IntakeAsync(
        SlackInbound inbound, PersonIdentity? requester, CancellationToken ct)
    {
        if (await operations.IsIntakePausedAsync(ct))
            return new IntakeResult(null, "Task intake is paused by an administrator. New requests are held until it resumes.");

        if (requester is null)
            return new IntakeResult(null,
                "Sorry — you're not recognised yet. Ask an administrator to map your Slack user before requesting work.");

        var correlationId = $"slack:{inbound.ChannelId}:{inbound.MessageTs}";
        if (await store.ExistsForCorrelationAsync(correlationId, ct))
            return new IntakeResult(null, "I've already received that request.");

        var now = clock.GetUtcNow();
        var task = new WorkTask(StripMention(inbound.Text), requester.Id.ToString(),
            TaskSource.Slack, correlationId, now);
        task.Fire(TaskTrigger.PlanningStarted, now);

        var envelope = new AuthorizationEnvelope(
            task.Id,
            requester.Id.ToString(),
            options.DefaultRepositoryIds
                .Select(id => new RepositoryScope(id, ["*"], ["/"], RepositoryAccess.Write))
                .ToList(),
            ["staging"],
            [],
            options.DefaultAllowedProviders,
            [Domain.Authorization.AgentStage.Implementation, Domain.Authorization.AgentStage.Review],
            [inbound.ChannelId],
            requester.SlackUserId is null ? [] : [requester.SlackUserId],
            [],
            [],
            options.DefaultBudget,
            now.AddDays(7),
            now);

        var ledger = new BudgetLedger(task.Id, options.DefaultBudget);
        var step = new Step(task.Id, "planning", null, null, now);
        var message = new OutboxMessage(
            "step.dispatch",
            JsonSerializer.Serialize(new { stepId = step.Id }),
            task.Id,
            $"step:{step.Id}",
            now);

        var created = await store.TryCreateAsync(
            new TaskIntakeGraph(task, envelope, ledger, step, message), ct);
        if (created is null)
            return new IntakeResult(null, "I've already received that request.");

        await audit.RecordAsync(new AuditEntry(
            Actor: $"slack:{requester.SlackUserId ?? requester.Id.ToString()}",
            TaskId: task.Id,
            Action: "task.intake",
            Reason: $"Task created from Slack message {correlationId}.",
            Outcome: "Success",
            CorrelationId: correlationId,
            Details: new Dictionary<string, string> { ["channel"] = inbound.ChannelId }), ct);

        return new IntakeResult(task.Id, $"Created task {task.Id} and started planning.");
    }

    internal static string StripMention(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("@orchestrator", StringComparison.OrdinalIgnoreCase))
            return trimmed["@orchestrator".Length..].Trim();
        return trimmed;
    }
}
