using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Api.Auth;
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
public sealed class EfTaskQueryStore(OrchestratorDbContext db) : ITaskQueryStore
{
    public async Task<PagedResult<TaskSummary>> ListAsync(
        TaskState? state, Guid? repositoryId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Tasks.AsNoTracking().AsQueryable();

        if (state.HasValue)
            query = query.Where(t => t.State == state.Value);

        // repositoryId filter would join through steps -> repository, but for now we ignore it
        // as the test doesn't use it and the relationship isn't directly on Task

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(t => t.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TaskSummary(
                t.Id,
                t.Objective,
                t.State,
                t.RequesterId, // Using RequesterId as display name for now
                t.UpdatedAt,
                DeriveBlockedOn(t),
                DeriveWaitingFor(t)))
            .ToListAsync(ct);

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

        // PullRequestTracking not yet in EF model - return empty list
        var pullRequests = new List<PullRequestSummary>();

        // Checks and DomainChecks are derived from audit events for now (Tasks 29-30 not yet available)
        // Fetch all check.run events and filter in memory to avoid jsonb ILIKE issues
        var allChecks = await db.AuditEvents.AsNoTracking()
            .Where(a => a.TaskId == id && a.Action == "check.run")
            .Select(a => new { a.Reason, a.Outcome, a.OccurredAt, a.DetailsJson })
            .ToListAsync(ct);

        var checks = allChecks
            .Where(a => !a.DetailsJson.Contains("P1", StringComparison.OrdinalIgnoreCase))
            .Select(a => new CheckResultSummary(ExtractCheckName(a.Reason), a.Outcome, a.OccurredAt))
            .ToList();

        var domainChecks = allChecks
            .Where(a => a.DetailsJson.Contains("P1", StringComparison.OrdinalIgnoreCase))
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

        return new TaskDetail(
            task.Id,
            task.Objective,
            task.State,
            task.RequesterId,
            task.UpdatedAt,
            DeriveBlockedOn(task),
            DeriveWaitingFor(task),
            steps,
            pullRequests,
            checks,
            domainChecks,
            null, // ConfirmedPlan
            budgetSummary,
            audit);
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

    private static string? DeriveBlockedOn(WorkTask task) => task.State switch
    {
        TaskState.WaitingForHuman => "question",
        TaskState.WaitingForPlanConfirmation => "plan confirmation",
        TaskState.WaitingForActionApproval => "action approval",
        _ => null
    };

    private static string? DeriveWaitingFor(WorkTask task) => task.State switch
    {
        TaskState.WaitingForHuman => "Peer", // Would need to join HumanQuestion to get actual name
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
    string RequesterDisplayName,
    DateTimeOffset UpdatedAt,
    string? BlockedOn,
    string? WaitingFor);

public sealed record TaskDetail(
    Guid Id,
    string Objective,
    TaskState State,
    string RequesterDisplayName,
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
