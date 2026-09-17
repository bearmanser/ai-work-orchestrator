namespace Orchestrator.Domain.Policy;

/// <summary>
/// The single persisted row backing the admin-editable parts of
/// <see cref="Orchestrator.Application.Policy.PolicyRules"/>. Deliberately a single
/// fixed-id row rather than a table keyed by name: there is exactly one active policy
/// configuration for the whole system, never one per task or per actor.
/// </summary>
public sealed class PolicyConfigurationRow
{
    /// <summary>The fixed identity of the single row this table ever holds.</summary>
    public const int SingletonId = 1;

    private PolicyConfigurationRow() { } // EF

    public PolicyConfigurationRow(string alwaysRequiresApprovalCsv, string forbiddenEnvironmentsCsv, DateTimeOffset updatedAt)
    {
        Id = SingletonId;
        AlwaysRequiresApprovalCsv = alwaysRequiresApprovalCsv;
        ForbiddenEnvironmentsCsv = forbiddenEnvironmentsCsv;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    public int Id { get; private set; }

    /// <summary>Comma-separated <see cref="Orchestrator.Domain.Authorization.ActionKind"/> names.</summary>
    public string AlwaysRequiresApprovalCsv { get; private set; } = null!;

    /// <summary>Comma-separated environment names, compared case-insensitively.</summary>
    public string ForbiddenEnvironmentsCsv { get; private set; } = null!;

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string alwaysRequiresApprovalCsv, string forbiddenEnvironmentsCsv, DateTimeOffset at)
    {
        AlwaysRequiresApprovalCsv = alwaysRequiresApprovalCsv;
        ForbiddenEnvironmentsCsv = forbiddenEnvironmentsCsv;
        UpdatedAt = at.ToUniversalTime();
    }
}
