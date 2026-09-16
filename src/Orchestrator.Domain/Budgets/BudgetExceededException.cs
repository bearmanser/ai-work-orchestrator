namespace Orchestrator.Domain.Budgets;

/// <summary>Which of the four budget dimensions was exhausted.</summary>
public enum BudgetDimension { Usd, WallClock, AgentInvocations, CorrectionAttempts }

/// <summary>
/// Thrown by <see cref="BudgetLedger.EnsureWithinLimits"/> when the task's cumulative
/// spend has exceeded one of its fixed limits. <see cref="Dimension"/> names the
/// specific limit that was exhausted so callers can surface the cause to a human.
/// </summary>
public sealed class BudgetExceededException(BudgetDimension dimension)
    : InvalidOperationException($"Budget exhausted: {dimension}")
{
    public BudgetDimension Dimension { get; } = dimension;
}
