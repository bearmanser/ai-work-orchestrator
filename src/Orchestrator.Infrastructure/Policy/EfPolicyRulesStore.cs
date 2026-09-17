using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Policy;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Policy;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Policy;

/// <summary>EF-backed <see cref="IPolicyRulesStore"/>, one fixed row in `policy_configuration`.</summary>
public sealed class EfPolicyRulesStore(OrchestratorDbContext db, TimeProvider clock) : IPolicyRulesStore
{
    private const char Separator = ',';

    public async Task<PolicyRules> GetAsync(CancellationToken ct)
    {
        var row = await db.PolicyConfigurations.SingleOrDefaultAsync(r => r.Id == PolicyConfigurationRow.SingletonId, ct);
        if (row is null)
            return PolicyRules.Default;

        var alwaysRequiresApproval = row.AlwaysRequiresApprovalCsv
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => Enum.Parse<ActionKind>(name))
            .ToHashSet();

        var forbiddenEnvironments = row.ForbiddenEnvironmentsCsv
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new PolicyRules(alwaysRequiresApproval, forbiddenEnvironments);
    }

    public async Task SetAsync(PolicyRules rules, CancellationToken ct)
    {
        var alwaysRequiresApprovalCsv = string.Join(Separator, rules.AlwaysRequiresApproval.Select(a => a.ToString()));
        var forbiddenEnvironmentsCsv = string.Join(Separator, rules.ForbiddenEnvironments);

        var row = await db.PolicyConfigurations.SingleOrDefaultAsync(r => r.Id == PolicyConfigurationRow.SingletonId, ct);
        if (row is null)
        {
            db.PolicyConfigurations.Add(new PolicyConfigurationRow(alwaysRequiresApprovalCsv, forbiddenEnvironmentsCsv, clock.GetUtcNow()));
        }
        else
        {
            row.Update(alwaysRequiresApprovalCsv, forbiddenEnvironmentsCsv, clock.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
    }
}
