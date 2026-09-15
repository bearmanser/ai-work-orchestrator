using Hangfire.Dashboard;

namespace Orchestrator.Api.Dashboard;

/// <summary>Denies all dashboard access until Task 12 wires real administrator auth.</summary>
public sealed class AdminOnlyDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => false;
}
