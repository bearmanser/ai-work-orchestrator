using System.Security.Claims;
using Orchestrator.Api.Auth;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Identity;

namespace Orchestrator.Api.Endpoints;

public sealed record PersonDto(
    Guid Id,
    string DisplayName,
    string? SlackUserId,
    string? AzureDevOpsId,
    string? Email,
    bool IsActive)
{
    public static PersonDto From(PersonIdentity p) => new(
        p.Id, p.DisplayName, p.SlackUserId, p.AzureDevOpsId, p.Email, p.IsActive);
}

public sealed record CreatePersonRequest(
    string DisplayName,
    string? SlackUserId,
    string? AzureDevOpsId,
    string? Email);

public sealed record OwnershipDto(
    Guid Id,
    string Subject,
    string SubjectKey,
    Guid OwnerPersonId,
    bool CanConfirmPlans,
    bool CanApproveRestrictedActions)
{
    public static OwnershipDto From(OwnershipEntry e) => new(
        e.Id, e.Subject.ToString(), e.SubjectKey, e.OwnerPersonId,
        e.CanConfirmPlans, e.CanApproveRestrictedActions);
}

public sealed record CreateOwnershipRequest(
    string Subject,
    string SubjectKey,
    Guid OwnerPersonId,
    bool CanConfirmPlans,
    bool CanApproveRestrictedActions);

/// <summary>Administrator management of the identity mapping and ownership directory.</summary>
public static class DirectoryEndpoints
{
    public static void Map(WebApplication app)
    {
        var people = app.MapGroup("/api/admin/people")
            .RequireAuthorization(AdminOnly.PolicyName);
        people.MapGet("/", ListPeople);
        people.MapPost("/", CreatePerson);
        people.MapDelete("/{id:guid}", DeletePerson);

        var ownership = app.MapGroup("/api/admin/ownership")
            .RequireAuthorization(AdminOnly.PolicyName);
        ownership.MapGet("/", ListOwnership);
        ownership.MapPost("/", CreateOwnership);
        ownership.MapDelete("/{id:guid}", DeleteOwnership);
    }

    private static async Task<IResult> ListPeople(IPersonStore store, CancellationToken ct)
    {
        var people = await store.ListAsync(ct);
        return Results.Json(people.Select(PersonDto.From).ToArray());
    }

    private static async Task<IResult> CreatePerson(
        CreatePersonRequest request, ClaimsPrincipal user,
        IPersonStore store, IAuditWriter audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Results.BadRequest(new { message = "Display name is required." });

        var person = new PersonIdentity(
            Guid.CreateVersion7(),
            request.DisplayName,
            request.SlackUserId,
            request.AzureDevOpsId,
            request.Email,
            true);
        await store.AddAsync(person, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: Actor(user),
            TaskId: null,
            Action: "directory.person.create",
            Reason: $"Person '{person.DisplayName}' added.",
            Outcome: "Success",
            CorrelationId: person.Id.ToString(),
            Details: new Dictionary<string, string> { ["personId"] = person.Id.ToString() }), ct);

        return Results.Created($"/api/admin/people/{person.Id}", PersonDto.From(person));
    }

    private static async Task<IResult> DeletePerson(
        Guid id, ClaimsPrincipal user,
        IPersonStore store, IAuditWriter audit, CancellationToken ct)
    {
        var person = await store.GetByIdAsync(id, ct);
        if (person is null)
            return Results.NotFound();

        await store.RemoveAsync(id, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: Actor(user),
            TaskId: null,
            Action: "directory.person.delete",
            Reason: $"Person '{person.DisplayName}' removed.",
            Outcome: "Success",
            CorrelationId: id.ToString(),
            Details: new Dictionary<string, string> { ["personId"] = id.ToString() }), ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListOwnership(IOwnershipStore store, CancellationToken ct)
    {
        var entries = await store.ListAsync(ct);
        return Results.Json(entries.Select(OwnershipDto.From).ToArray());
    }

    private static async Task<IResult> CreateOwnership(
        CreateOwnershipRequest request, ClaimsPrincipal user,
        IPersonStore people, IOwnershipStore store, IAuditWriter audit, CancellationToken ct)
    {
        if (!Enum.TryParse<OwnershipSubject>(request.Subject, ignoreCase: true, out var subject))
            return Results.BadRequest(new { message = $"Unknown ownership subject '{request.Subject}'." });

        if (string.IsNullOrWhiteSpace(request.SubjectKey))
            return Results.BadRequest(new { message = "Subject key is required." });

        if (await people.GetByIdAsync(request.OwnerPersonId, ct) is null)
            return Results.NotFound();

        var entry = new OwnershipEntry(
            Guid.CreateVersion7(),
            subject,
            request.SubjectKey,
            request.OwnerPersonId,
            request.CanConfirmPlans,
            request.CanApproveRestrictedActions);
        await store.AddAsync(entry, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: Actor(user),
            TaskId: null,
            Action: "directory.ownership.create",
            Reason: $"Ownership for '{entry.SubjectKey}' granted.",
            Outcome: "Success",
            CorrelationId: entry.Id.ToString(),
            Details: new Dictionary<string, string> { ["ownershipId"] = entry.Id.ToString() }), ct);

        return Results.Created($"/api/admin/ownership/{entry.Id}", OwnershipDto.From(entry));
    }

    private static async Task<IResult> DeleteOwnership(
        Guid id, ClaimsPrincipal user,
        IOwnershipStore store, IAuditWriter audit, CancellationToken ct)
    {
        var entry = await store.GetByIdAsync(id, ct);
        if (entry is null)
            return Results.NotFound();

        await store.RemoveAsync(id, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: Actor(user),
            TaskId: null,
            Action: "directory.ownership.delete",
            Reason: $"Ownership '{id}' removed.",
            Outcome: "Success",
            CorrelationId: id.ToString(),
            Details: new Dictionary<string, string> { ["ownershipId"] = id.ToString() }), ct);

        return Results.NoContent();
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.Identity?.Name ?? "unknown";
}
