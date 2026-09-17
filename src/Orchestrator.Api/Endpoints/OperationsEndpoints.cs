using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Orchestrator.Api.Auth;
using Orchestrator.Application.Operations;

namespace Orchestrator.Api.Endpoints;

/// <summary>Administrative operational controls.</summary>
public static class OperationsEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/operations")
            .RequireAuthorization(AdminOnly.PolicyName);

        group.MapPost("/pause-intake", PauseIntake);
        group.MapPost("/resume-intake", ResumeIntake);

        var integrations = group.MapGroup("/integrations");
        integrations.MapPost("/{name}/disable", DisableIntegration);
        integrations.MapPost("/{name}/enable", EnableIntegration);
    }

    private static async Task<Results<NoContent, BadRequest<string>>> PauseIntake(
        ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        await service.PauseIntakeAsync(actor, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, BadRequest<string>>> ResumeIntake(
        ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        await service.ResumeIntakeAsync(actor, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, BadRequest<string>>> DisableIntegration(
        string name, ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        await service.DisableIntegrationAsync(name, actor, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, BadRequest<string>>> EnableIntegration(
        string name, ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        await service.EnableIntegrationAsync(name, actor, ct);
        return TypedResults.NoContent();
    }
}

/// <summary>Task-level operational controls.</summary>
public static class TaskOperationsEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/tasks")
            .RequireAuthorization(AdminOnly.PolicyName);

        group.MapPost("/{taskId:guid}/cancel", CancelTask);
        group.MapPost("/{taskId:guid}/revoke-authorization", RevokeAuthorization);
        group.MapPost("/{taskId:guid}/steps/{stepId:guid}/retry", RetryStep);
        group.MapPost("/{taskId:guid}/reconcile", ReconcileTask);
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>>> CancelTask(
        Guid taskId, ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        try
        {
            await service.CancelTaskAsync(taskId, actor, ct);
            return TypedResults.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>>> RevokeAuthorization(
        Guid taskId, ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        try
        {
            await service.RevokeAuthorizationAsync(taskId, actor, ct);
            return TypedResults.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
    }

    private static async Task<Results<Accepted, Conflict, NotFound, BadRequest<string>>> RetryStep(
        Guid taskId, Guid stepId, ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        try
        {
            await service.RetryStepAsync(taskId, stepId, actor, ct);
            return TypedResults.Accepted((string?)null);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unknown tool call outcomes"))
        {
            return TypedResults.Conflict();
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
    }

    private static async Task<Results<Ok<ReconcileResponse>, NotFound, BadRequest<string>>> ReconcileTask(
        Guid taskId, ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        try
        {
            var summary = await service.ReconcileTaskAsync(taskId, actor, ct);
            return TypedResults.Ok(new ReconcileResponse(
                summary.Resolved,
                summary.Failed,
                summary.StillUnknown,
                summary.UnresolvedTargets.Select(t => t.ToString()).ToArray(),
                summary.FullyReconciled
                    ? $"Reconciled {summary.Resolved} succeeded and {summary.Failed} failed operation(s)."
                    : $"Could not reconcile {summary.StillUnknown} operation(s) - no reconciler registered for: " +
                      $"{string.Join(", ", summary.UnresolvedTargets)}."));
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
    }
}

/// <summary>
/// Distinguishes "checked and it was fine" (<see cref="Resolved"/>/<see cref="Failed"/>)
/// from "nobody checked" (<see cref="StillUnknown"/> greater than zero,
/// <see cref="UnresolvedTargets"/> naming why) - an operator reading this response
/// must be able to tell the two apart.
/// </summary>
public sealed record ReconcileResponse(
    int Resolved, int Failed, int StillUnknown, IReadOnlyList<string> UnresolvedTargets, string Message);
