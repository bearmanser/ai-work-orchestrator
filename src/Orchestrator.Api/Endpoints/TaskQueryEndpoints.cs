using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Api.Auth;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Audit;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Api.Endpoints;

/// <summary>Read-only projection queries for task inspection (admin).</summary>
public interface ITaskQueryStore
{
    Task<PagedResult<TaskSummary>> ListAsync(TaskState? state, Guid? repositoryId, int page, int pageSize, CancellationToken ct);
    Task<TaskDetail?> GetAsync(Guid id, CancellationToken ct);
    Task<Stream?> OpenArtifactAsync(string artifactId, CancellationToken ct);
    Task<Artifact?> GetArtifactMetadataAsync(string artifactId, CancellationToken ct);
}

/// <summary>EF-backed implementation using projection selects to avoid leaking secrets.</summary>
public sealed class EfTaskQueryStore(OrchestratorDbContext db, IPersonStore people) : ITaskQueryStore
{
    /// <summary>Question statuses that mean a task is still genuinely waiting on the recipient.</summary>
    private static readonly HumanQuestionStatus[] OpenQuestionStatuses =
        [HumanQuestionStatus.Open, HumanQuestionStatus.ReminderSent, HumanQuestionStatus.Escalated];

    public async Task<PagedResult<TaskSummary>> ListAsync(
        TaskState? state, Guid? repositoryId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Tasks.AsNoTracking().AsQueryable();

        if (state.HasValue)
            query = query.Where(t => t.State == state.Value);

        if (repositoryId.HasValue)
        {
            // A task's repository association traces through the isolated checkout
            // recorded for it in Workspace (task 34's git-workspace record), not
            // through a plan-level entity - no plan/repository-change table exists
            // yet in this codebase.
            var taskIdsForRepository = db.Workspaces.AsNoTracking()
                .Where(w => w.RepositoryId == repositoryId.Value)
                .Select(w => w.TaskId);
            query = query.Where(t => taskIdsForRepository.Contains(t.Id));
        }

        var totalCount = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(t => t.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new { t.Id, t.Objective, t.State, t.RequesterId, t.UpdatedAt })
            .ToListAsync(ct);

        var waitingFor = await ResolveWaitingForAsync(rows.Select(r => r.Id).ToList(), ct);

        var items = rows
            .Select(t => new TaskSummary(
                t.Id,
                t.Objective,
                t.State,
                t.RequesterId,
                t.UpdatedAt,
                DeriveBlockedOn(t.State),
                waitingFor.GetValueOrDefault(t.Id)))
            .ToList();

        return new PagedResult<TaskSummary>(items, page, pageSize, totalCount);
    }

    public async Task<TaskDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
            return null;

        var steps = await db.Steps.AsNoTracking()
            .Where(s => s.TaskId == id)
            .Select(s => new StepSummary(s.Id, s.Capability, s.Status, s.CreatedAt))
            .ToListAsync(ct);

        // PullRequestTracking (Task 23's domain type) has no DbSet, EF configuration,
        // or migration anywhere in this codebase yet, so there is nothing to query -
        // this is a persistence gap in whichever task owns that schema, not task 45.
        // Left genuinely empty rather than fabricated, so callers cannot mistake this
        // for "no PRs" once that table exists.
        var pullRequests = new List<PullRequestSummary>();

        // Checks/DomainChecks: Tasks 29 (runtime check runner) and 30 (evidence
        // bundles) don't exist yet, so there is no CheckResult/DomainCheckResult
        // table to join against. As a placeholder, we derive both lists from
        // "check.run" audit events and use IsDomainCheck to split them by reading
        // a structured "domain" field out of DetailsJson - never by substring-
        // matching the raw JSON blob, which could misclassify any check whose
        // free-text reason happens to contain "P1". Replace this once 29/30 land.
        var allChecks = await db.AuditEvents.AsNoTracking()
            .Where(a => a.TaskId == id && a.Action == "check.run")
            .Select(a => new { a.Reason, a.Outcome, a.OccurredAt, a.DetailsJson })
            .ToListAsync(ct);

        var checks = allChecks
            .Where(a => !IsDomainCheck(a.DetailsJson))
            .Select(a => new CheckResultSummary(ExtractCheckName(a.Reason), a.Outcome, a.OccurredAt))
            .ToList();

        var domainChecks = allChecks
            .Where(a => IsDomainCheck(a.DetailsJson))
            .Select(a => new DomainCheckSummary(ExtractCheckName(a.Reason), a.Outcome, a.OccurredAt))
            .ToList();

        var audit = await db.AuditEvents.AsNoTracking()
            .Where(a => a.TaskId == id)
            .OrderBy(a => a.OccurredAt)
            .Select(a => new AuditEntrySummary(
                a.Id, a.Actor, a.Action, a.Reason, a.Outcome, a.OccurredAt))
            .ToListAsync(ct);

        // Budget from BudgetLedger
        var budget = await db.Budgets.AsNoTracking()
            .FirstOrDefaultAsync(b => b.TaskId == id, ct);
        var budgetSummary = budget is not null
            ? new BudgetSummary(
                budget.Limits.MaxUsd, budget.SpentUsd,
                budget.Limits.MaxWallClock, budget.ElapsedWallClock,
                budget.Limits.MaxAgentInvocations, budget.AgentInvocations,
                budget.Limits.MaxCorrectionAttempts, budget.CorrectionAttempts)
            : new BudgetSummary(0, 0, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, 0);

        // ConfirmedPlan - not available yet, leave null

        var waitingFor = await ResolveWaitingForAsync(id, task.State, ct);

        return new TaskDetail(
            task.Id,
            task.Objective,
            task.State,
            task.RequesterId,
            task.UpdatedAt,
            DeriveBlockedOn(task.State),
            waitingFor,
            steps,
            pullRequests,
            checks,
            domainChecks,
            null, // ConfirmedPlan
            budgetSummary,
            audit);
    }

    /// <summary>
    /// Resolves who a single <see cref="TaskState.WaitingForHuman"/> task is
    /// waiting for, by finding its still-open <see cref="HumanQuestion"/> and
    /// resolving the recipient's display name through <see cref="IPersonStore"/> -
    /// the same identity-resolution pattern <c>HumanQuestionService</c> uses.
    /// Returns null if the task isn't waiting on a human, has no open question,
    /// or the recipient can no longer be resolved to a display name.
    /// </summary>
    private async Task<string?> ResolveWaitingForAsync(Guid taskId, TaskState state, CancellationToken ct)
    {
        if (state != TaskState.WaitingForHuman)
            return null;

        var recipientId = await db.HumanQuestions.AsNoTracking()
            .Where(q => q.TaskId == taskId && OpenQuestionStatuses.Contains(q.Status))
            .OrderByDescending(q => q.AskedAt)
            .Select(q => (Guid?)q.RecipientPersonId)
            .FirstOrDefaultAsync(ct);

        if (recipientId is null)
            return null;

        var person = await people.GetByIdAsync(recipientId.Value, ct);
        return person?.DisplayName;
    }

    /// <summary>Batch form of <see cref="ResolveWaitingForAsync(Guid,TaskState,CancellationToken)"/> for a page of tasks.</summary>
    private async Task<Dictionary<Guid, string?>> ResolveWaitingForAsync(IReadOnlyList<Guid> taskIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string?>();
        if (taskIds.Count == 0)
            return result;

        var openQuestions = await db.HumanQuestions.AsNoTracking()
            .Where(q => taskIds.Contains(q.TaskId) && OpenQuestionStatuses.Contains(q.Status))
            .OrderByDescending(q => q.AskedAt)
            .Select(q => new { q.TaskId, q.RecipientPersonId })
            .ToListAsync(ct);

        // Keep only the most recently asked open question per task.
        var recipientByTask = openQuestions
            .GroupBy(q => q.TaskId)
            .ToDictionary(g => g.Key, g => g.First().RecipientPersonId);

        var uniquePersonIds = recipientByTask.Values.Distinct().ToList();
        var displayNames = new Dictionary<Guid, string?>();
        foreach (var personId in uniquePersonIds)
        {
            var person = await people.GetByIdAsync(personId, ct);
            displayNames[personId] = person?.DisplayName;
        }

        foreach (var taskId in taskIds)
        {
            result[taskId] = recipientByTask.TryGetValue(taskId, out var personId)
                ? displayNames.GetValueOrDefault(personId)
                : null;
        }

        return result;
    }

    /// <summary>
    /// True when a "check.run" audit event's <c>DetailsJson</c> carries a
    /// structured <c>"domain": "P1"</c> field. This is a placeholder pending
    /// Tasks 29/30's real evidence schema (CheckResult/DomainCheckResult) -
    /// once those tables exist, this heuristic should be replaced by a real
    /// join. Until then, matching a structured field (rather than searching
    /// the raw JSON text for the substring "P1") avoids misclassifying a
    /// local check whose free-text reason happens to mention "P1", and
    /// malformed/missing JSON is treated as "not a domain check" rather than
    /// throwing.
    /// </summary>
    private static bool IsDomainCheck(string detailsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("domain", out var domain)
                && domain.ValueKind == JsonValueKind.String
                && string.Equals(domain.GetString(), "P1", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<Stream?> OpenArtifactAsync(string artifactId, CancellationToken ct)
    {
        var artifact = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artifactId, ct);
        if (artifact is null)
            return null;

        var path = Path.Combine("artifacts", artifact.TaskId.ToString("N"), artifactId + ".bin");
        if (!File.Exists(path))
            return null;

        return File.OpenRead(path);
    }

    public async Task<Artifact?> GetArtifactMetadataAsync(string artifactId, CancellationToken ct)
    {
        return await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artifactId, ct);
    }

    private static string? DeriveBlockedOn(TaskState state) => state switch
    {
        TaskState.WaitingForHuman => "question",
        TaskState.WaitingForPlanConfirmation => "plan confirmation",
        TaskState.WaitingForActionApproval => "action approval",
        _ => null
    };

    private static string ExtractCheckName(string reason)
    {
        // Simple extraction - in reality would be more sophisticated
        return reason.Split(':').LastOrDefault()?.Trim() ?? reason;
    }
}

/// <summary>In-memory implementation for tests without database.</summary>
public sealed class InMemoryTaskQueryStore : ITaskQueryStore
{
    public Task<PagedResult<TaskSummary>> ListAsync(TaskState? state, Guid? repositoryId, int page, int pageSize, CancellationToken ct)
        => Task.FromResult(new PagedResult<TaskSummary>([], page, pageSize, 0));

    public Task<TaskDetail?> GetAsync(Guid id, CancellationToken ct)
        => Task.FromResult<TaskDetail?>(null);

    public Task<Stream?> OpenArtifactAsync(string artifactId, CancellationToken ct)
        => Task.FromResult<Stream?>(null);

    public Task<Artifact?> GetArtifactMetadataAsync(string artifactId, CancellationToken ct)
        => Task.FromResult<Artifact?>(null);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record TaskSummary(
    Guid Id,
    string Objective,
    TaskState State,
    // Honestly named: this is the raw requester id (e.g. a Slack user id),
    // not a resolved display name - no identity lookup is done for the
    // requester today.
    string RequesterId,
    DateTimeOffset UpdatedAt,
    string? BlockedOn,
    string? WaitingFor);

public sealed record TaskDetail(
    Guid Id,
    string Objective,
    TaskState State,
    string RequesterId,
    DateTimeOffset UpdatedAt,
    string? BlockedOn,
    string? WaitingFor,
    IReadOnlyList<StepSummary> Steps,
    IReadOnlyList<PullRequestSummary> PullRequests,
    IReadOnlyList<CheckResultSummary> Checks,
    IReadOnlyList<DomainCheckSummary> DomainChecks,
    PlanRevisionSummary? ConfirmedPlan,
    BudgetSummary Budget,
    IReadOnlyList<AuditEntrySummary> Audit);

public sealed record StepSummary(Guid Id, string Capability, StepStatus Status, DateTimeOffset CreatedAt);

public sealed record PullRequestSummary(int PullRequestId, string Url, bool IsRequired, bool IsApproved, string? ApprovalReason);

public sealed record CheckResultSummary(string Name, string Outcome, DateTimeOffset RunAt);

public sealed record DomainCheckSummary(string Name, string Outcome, DateTimeOffset RunAt);

public sealed record PlanRevisionSummary(int Revision, DateTimeOffset ConfirmedAt);

public sealed record BudgetSummary(
    decimal MaxUsd,
    decimal SpentUsd,
    TimeSpan MaxWallClock,
    TimeSpan ElapsedWallClock,
    int MaxAgentInvocations,
    int AgentInvocations,
    int MaxCorrectionAttempts,
    int CorrectionAttempts);

public sealed record AuditEntrySummary(
    Guid Id,
    string Actor,
    string Action,
    string Reason,
    string Outcome,
    DateTimeOffset OccurredAt);

/// <summary>Administrator inspection of tasks and their evidence.</summary>
public static class TaskQueryEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/tasks")
            .RequireAuthorization(AdminOnly.PolicyName);

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{taskId:guid}/artifacts/{artifactId}", GetArtifactAsync);
    }

    private static async Task<Results<Ok<PagedResult<TaskSummary>>, BadRequest<string>>> ListAsync(
        TaskState? state, Guid? repositoryId, int page = 1, int pageSize = 20,
        ITaskQueryStore store = default!, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await store.ListAsync(state, repositoryId, page, pageSize, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<TaskDetail>, NotFound>> GetAsync(
        Guid id, ITaskQueryStore store = default!, CancellationToken ct = default)
    {
        var detail = await store.GetAsync(id, ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<FileStreamHttpResult, NotFound, ForbidHttpResult, UnauthorizedHttpResult>> GetArtifactAsync(
        Guid taskId, string artifactId,
        ITaskQueryStore store = default!, IArtifactStore artifactStore = default!,
        ClaimsPrincipal? user = default, CancellationToken ct = default)
    {
        var artifact = await store.GetArtifactMetadataAsync(artifactId, ct);
        if (artifact is null || artifact.TaskId != taskId)
            return TypedResults.NotFound();

        // Authorization: Restricted artifacts require admin (already enforced by AdminOnly policy)
        // Non-admin check would be done by a separate policy if needed
        if (artifact.Sensitivity == ArtifactSensitivity.Restricted)
        {
            // The AdminOnly policy already ensures only admins can reach this endpoint
            // If we had non-admin users, we'd check here
        }

        Stream stream;
        try
        {
            stream = await artifactStore.OpenAsync(artifactId, ct);
        }
        catch (ArtifactNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (ArtifactIntegrityException)
        {
            return TypedResults.NotFound();
        }

        if (stream is null)
            return TypedResults.NotFound();

        var fileName = $"{artifact.Type}-{artifactId}.{GetExtension(artifact.ContentType)}";
        return TypedResults.File(stream, artifact.ContentType, fileName, enableRangeProcessing: true);
    }

    private static string GetExtension(string contentType) => contentType switch
    {
        "text/plain" => "txt",
        "application/json" => "json",
        "application/octet-stream" => "bin",
        "text/x-diff" => "diff",
        "text/x-patch" => "patch",
        _ => "bin"
    };
}
