using Orchestrator.Domain.Authorization;

namespace Orchestrator.Application.Policy;

/// <summary>
/// The data-driven part of policy evaluation. Kept as plain sets rather than code
/// branches so an admin page can extend either list later without touching
/// <see cref="PolicyEngine"/>.
/// </summary>
public sealed record PolicyRules(
    IReadOnlySet<ActionKind> AlwaysRequiresApproval,
    IReadOnlySet<string> ForbiddenEnvironments)
{
    /// <summary>
    /// The default rule set: <see cref="ActionKind.DomainWrite"/> always requires
    /// approval even when an envelope covers it, because <see cref="AuthorizationEnvelope.Covers"/>
    /// gives P1 resources only a flat allow-list with no separate read/write axis - see
    /// the remarks on <see cref="AuthorizationEnvelope.Covers"/>. "production" and
    /// "prod" are unconditionally forbidden P1 environments, compared ordinally and
    /// case-insensitively.
    /// </summary>
    public static PolicyRules Default { get; } = new(
        AlwaysRequiresApproval: new HashSet<ActionKind> { ActionKind.DomainWrite },
        ForbiddenEnvironments: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "production", "prod" });
}
