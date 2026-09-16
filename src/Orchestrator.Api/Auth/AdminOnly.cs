namespace Orchestrator.Api.Auth;

/// <summary>
/// The authorization policy name for administrator-only API endpoints. Every
/// <c>/api/admin/*</c> endpoint must carry
/// <c>.RequireAuthorization(AdminOnly.PolicyName)</c>.
/// </summary>
public static class AdminOnly
{
    public const string PolicyName = "AdminOnly";
}
