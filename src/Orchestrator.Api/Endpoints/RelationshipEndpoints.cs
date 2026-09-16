using System.Security.Claims;
using Orchestrator.Api.Auth;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Api.Endpoints;

public sealed record AssertRelationshipRequest(Guid TargetRepositoryId, string RelationshipType, string? Note);

public sealed record RelationshipDto(
    Guid Id,
    Guid SourceRepositoryId,
    Guid TargetRepositoryId,
    string RelationshipType,
    string Status,
    IReadOnlyList<RelationshipEvidence> Evidence,
    DateTimeOffset LastVerifiedAt,
    string? LastVerifiedCommit,
    string? AdminNote)
{
    public static RelationshipDto From(RepositoryRelationship r) => new(
        r.Id, r.SourceRepositoryId, r.TargetRepositoryId,
        r.RelationshipType.ToString(), r.Status.ToString(), r.Evidence,
        r.LastVerifiedAt, r.LastVerifiedCommit, r.AdminNote);
}

/// <summary>Administrator inspection and assertion of repository relationships.</summary>
public static class RelationshipEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/repositories/{id:guid}/relationships")
            .RequireAuthorization(AdminOnly.PolicyName);
        group.MapGet("/", List);
        group.MapPost("/", Assert);

        app.MapDelete("/api/admin/relationships/{relationshipId:guid}", Delete)
            .RequireAuthorization(AdminOnly.PolicyName);
    }

    private static async Task<IResult> List(
        Guid id, IRepositoryProfileStore profiles, IRelationshipStore store, CancellationToken ct)
    {
        if (await profiles.GetByIdAsync(id, ct) is null)
            return Results.NotFound();

        var relationships = await store.ListForRepositoryAsync(id, ct);
        return Results.Json(relationships.Select(RelationshipDto.From).ToArray());
    }

    private static async Task<IResult> Assert(
        Guid id, AssertRelationshipRequest request, ClaimsPrincipal user,
        IRepositoryProfileStore profiles, IRelationshipStore store,
        TimeProvider clock, CancellationToken ct)
    {
        if (await profiles.GetByIdAsync(id, ct) is null
            || await profiles.GetByIdAsync(request.TargetRepositoryId, ct) is null)
            return Results.NotFound();

        if (!Enum.TryParse<RelationshipType>(request.RelationshipType, ignoreCase: true, out var type))
            return Results.BadRequest(new { message = $"Unknown relationship type '{request.RelationshipType}'." });

        var relationship = RepositoryRelationship.AssertByAdmin(
            id, request.TargetRepositoryId, type,
            request.Note ?? $"Asserted by {user.Identity?.Name ?? "unknown"}.", clock.GetUtcNow());
        await store.AddAsync(relationship, ct);

        return Results.Created(
            $"/api/admin/relationships/{relationship.Id}", RelationshipDto.From(relationship));
    }

    private static async Task<IResult> Delete(
        Guid relationshipId, IRelationshipStore store, CancellationToken ct)
    {
        var relationship = await store.GetByIdAsync(relationshipId, ct);
        if (relationship is null)
            return Results.NotFound();

        // Automation-derived evidence outranks deletion: only a human assertion
        // (possibly conflicted) may be removed, and only through this endpoint.
        if (relationship.Status == RelationshipStatus.Evidenced)
            return Results.Conflict(new
            {
                message = "Evidenced relationships cannot be deleted. " +
                    "Remove the underlying source evidence or assert a correction instead.",
            });

        await store.RemoveAsync(relationship, ct);
        return Results.NoContent();
    }
}
