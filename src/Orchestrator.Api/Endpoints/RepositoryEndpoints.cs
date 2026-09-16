using System.Security.Claims;
using Orchestrator.Api.Auth;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Api.Endpoints;

public sealed record RepositoryProfileDto(
    Guid Id,
    string Name,
    string RemoteUrl,
    string DefaultBranch,
    string Purpose,
    string OwnerContactId,
    string AccessPolicy,
    BuildCommands BuildCommands,
    IReadOnlyList<string> InstructionFilePaths,
    string? LastInspectedCommit,
    DateTimeOffset? LastInspectedAt,
    uint Version)
{
    public static RepositoryProfileDto From(RepositoryProfile r) => new(
        r.Id, r.Name, r.RemoteUrl, r.DefaultBranch, r.Purpose, r.OwnerContactId,
        r.AccessPolicy.ToString(), r.BuildCommands, r.InstructionFilePaths(),
        r.LastInspectedCommit, r.LastInspectedAt, r.Version);
}

/// <summary>Administrator management of the repository catalog.</summary>
public static class RepositoryEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/repositories")
            .RequireAuthorization(AdminOnly.PolicyName);
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}", GetById);
        group.MapPut("/{id:guid}", Update);
    }

    private static async Task<IResult> List(
        RepositoryCatalogService catalog, CancellationToken ct)
    {
        var profiles = await catalog.ListAsync(ct);
        return Results.Json(profiles.Select(RepositoryProfileDto.From).ToArray());
    }

    private static async Task<IResult> Create(
        CreateRepositoryRequest request, ClaimsPrincipal user,
        RepositoryCatalogService catalog, CancellationToken ct)
    {
        try
        {
            var created = await catalog.CreateAsync(request, Actor(user), ct);
            return Results.Created(
                $"/api/admin/repositories/{created.Id}", RepositoryProfileDto.From(created));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
    }

    private static async Task<IResult> GetById(
        Guid id, RepositoryCatalogService catalog, CancellationToken ct)
    {
        var profile = await catalog.GetAsync(id, ct);
        return profile is null ? Results.NotFound() : Results.Json(RepositoryProfileDto.From(profile));
    }

    private static async Task<IResult> Update(
        Guid id, UpdateRepositoryRequest request, HttpContext http, ClaimsPrincipal user,
        RepositoryCatalogService catalog, CancellationToken ct)
    {
        if (!http.Request.Headers.TryGetValue("If-Match", out var values)
            || !uint.TryParse(values.ToString(), out var expectedVersion))
            return Results.BadRequest(new { message = "If-Match header with the expected version is required." });

        try
        {
            var updated = await catalog.UpdateAsync(id, request, expectedVersion, Actor(user), ct);
            return Results.Json(RepositoryProfileDto.From(updated));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ConcurrentModificationException ex)
        {
            return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
        }
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.Identity?.Name ?? "unknown";
}
