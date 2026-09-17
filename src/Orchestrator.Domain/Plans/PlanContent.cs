using Orchestrator.Domain.Budgets;

namespace Orchestrator.Domain.Plans;

public enum ChangeIntent
{
    ReferenceOnly,
    Change,
}

public sealed record PlannedRepositoryChange(
    Guid RepositoryId,
    string RepositoryName,
    ChangeIntent Intent,
    string BaselineCommit);

public sealed record PlanContent(
    string Objective,
    IReadOnlyList<string> Scope,
    IReadOnlyList<string> Exclusions,
    string Approach,
    IReadOnlyList<PlannedRepositoryChange> RepositoryChanges,
    IReadOnlyList<string> Tests,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> OpenQuestions,
    BudgetLimits Budget,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> P1Checks);
