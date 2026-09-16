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
            return TypedResults.Accepted();
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

    private static async Task<Results<NoContent, NotFound, BadRequest<string>>> ReconcileTask(
        Guid taskId, ClaimsPrincipal user,
        OperationalControlService service,
        CancellationToken ct)
    {
        var actor = user.Identity?.Name ?? "unknown";
        try
        {
            await service.ReconcileTaskAsync(taskId, actor, ct);
            return TypedResults.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return TypedResults.NotFound();
        }
    }
}