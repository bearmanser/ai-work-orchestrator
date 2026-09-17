using System.Security.Claims;
using Orchestrator.Api.Auth;
using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;

namespace Orchestrator.Api.Endpoints;

/// <summary>Wire shape of <see cref="PolicyRules"/> for the admin settings page.</summary>
public sealed record PolicyRulesDto(
    IReadOnlyList<string> AlwaysRequiresApproval,
    IReadOnlyList<string> ForbiddenEnvironments)
{
    public static PolicyRulesDto From(PolicyRules rules) => new(
        rules.AlwaysRequiresApproval.Select(a => a.ToString()).ToArray(),
        rules.ForbiddenEnvironments.ToArray());
}

/// <summary>
/// PUT /api/admin/policy body. Either field left null keeps the corresponding part of
/// the current rules unchanged, so an admin can update one axis without having to
/// resend the other.
/// </summary>
public sealed record UpdatePolicyRulesRequest(
    IReadOnlyList<string>? AlwaysRequiresApproval,
    IReadOnlyList<string>? ForbiddenEnvironments);

/// <summary>
/// Admin configuration of <see cref="PolicyRules"/> - the data-driven part of
/// <see cref="PolicyEngine"/>'s decisions. "production" and "prod" are a documented,
/// unconditional non-goal (see the remarks on <see cref="PolicyRules.Default"/>), not a
/// setting an admin can turn off, so this endpoint refuses any update that would drop an
/// environment that is currently forbidden - an admin can only add further forbidden
/// environments, never remove one.
/// </summary>
public static class PolicyEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/policy")
            .RequireAuthorization(AdminOnly.PolicyName);
        group.MapGet("/", Get);
        group.MapPut("/", Update);
    }

    private static async Task<IResult> Get(IPolicyRulesStore store, CancellationToken ct)
    {
        var rules = await store.GetAsync(ct);
        return Results.Json(PolicyRulesDto.From(rules));
    }

    private static async Task<IResult> Update(
        UpdatePolicyRulesRequest request, ClaimsPrincipal user,
        IPolicyRulesStore store, IAuditWriter audit, CancellationToken ct)
    {
        var current = await store.GetAsync(ct);

        var forbiddenEnvironments = current.ForbiddenEnvironments;
        if (request.ForbiddenEnvironments is not null)
        {
            var requested = new HashSet<string>(request.ForbiddenEnvironments, StringComparer.OrdinalIgnoreCase);
            var removed = current.ForbiddenEnvironments.Where(e => !requested.Contains(e)).ToArray();
            if (removed.Length > 0)
            {
                return Results.BadRequest(new
                {
                    message = $"Cannot remove {string.Join(", ", removed.Select(e => $"'{e}'"))} from forbidden " +
                               "environments; production is a non-goal, not a configurable setting.",
                });
            }

            forbiddenEnvironments = requested;
        }

        var alwaysRequiresApproval = current.AlwaysRequiresApproval;
        if (request.AlwaysRequiresApproval is not null)
        {
            var parsed = new HashSet<ActionKind>();
            foreach (var name in request.AlwaysRequiresApproval)
            {
                if (!Enum.TryParse<ActionKind>(name, ignoreCase: true, out var action))
                    return Results.BadRequest(new { message = $"Unknown action kind '{name}'." });
                parsed.Add(action);
            }

            alwaysRequiresApproval = parsed;
        }

        var updated = new PolicyRules(alwaysRequiresApproval, forbiddenEnvironments);
        await store.SetAsync(updated, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: user.Identity?.Name ?? "unknown",
            TaskId: null,
            Action: "policy.configure",
            Reason: "Policy rules updated by administrator.",
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string>
            {
                ["alwaysRequiresApproval"] = string.Join(',', alwaysRequiresApproval),
                ["forbiddenEnvironments"] = string.Join(',', forbiddenEnvironments),
            }), ct);

        return Results.NoContent();
    }
}
