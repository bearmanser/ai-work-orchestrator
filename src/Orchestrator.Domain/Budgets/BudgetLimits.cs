namespace Orchestrator.Domain.Budgets;

/// <summary>
/// The fixed ceiling for each of the four budget dimensions a task's agent loop
/// is bounded by. Persisted as an owned type on <see cref="BudgetLedger"/>.
/// </summary>
public sealed record BudgetLimits(
    decimal MaxUsd, TimeSpan MaxWallClock, int MaxAgentInvocations, int MaxCorrectionAttempts);
