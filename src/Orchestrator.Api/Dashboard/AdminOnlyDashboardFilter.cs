using Hangfire.Dashboard;

namespace Orchestrator.Api.Dashboard;

/// <summary>Allows Hangfire dashboard access only to signed-in administrators.</summary>
public sealed class AdminOnlyDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        context.GetHttpContext().User.Identity?.IsAuthenticated is true
        && context.GetHttpContext().User.HasClaim("admin", "true");
}
