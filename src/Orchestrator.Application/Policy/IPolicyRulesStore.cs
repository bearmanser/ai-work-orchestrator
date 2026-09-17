namespace Orchestrator.Application.Policy;

/// <summary>
/// Persists the admin-editable <see cref="PolicyRules"/>. <see cref="GetAsync"/> returns
/// <see cref="PolicyRules.Default"/> until an admin has ever saved a change, so the system
/// behaves identically to today until someone opts into the settings page.
/// </summary>
public interface IPolicyRulesStore
{
    Task<PolicyRules> GetAsync(CancellationToken ct);

    Task SetAsync(PolicyRules rules, CancellationToken ct);
}
