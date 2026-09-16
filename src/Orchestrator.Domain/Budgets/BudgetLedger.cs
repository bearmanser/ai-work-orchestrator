namespace Orchestrator.Domain.Budgets;

/// <summary>How much headroom remains in each of the four budget dimensions.</summary>
public sealed record BudgetRemaining(
    decimal Usd, TimeSpan WallClock, int AgentInvocations, int CorrectionAttempts);

/// <summary>
/// Cumulative per-task spend against a fixed <see cref="BudgetLimits"/>. A task has
/// exactly one ledger for its whole life: every feedback cycle (a new round of PR
/// review, a retried step) charges the same ledger rather than starting a fresh one,
/// so an agent loop cannot dodge its limits by looking like a new attempt each time.
/// </summary>
public sealed class BudgetLedger
{
    private BudgetLedger() { }

    public BudgetLedger(Guid taskId, BudgetLimits limits)
    {
        Id = Guid.CreateVersion7();
        TaskId = taskId;
        Limits = limits;
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public BudgetLimits Limits { get; private set; } = null!;
    public decimal SpentUsd { get; private set; }
    public TimeSpan ElapsedWallClock { get; private set; }
    public int AgentInvocations { get; private set; }
    public int CorrectionAttempts { get; private set; }

    /// <summary>
    /// Adds spend to the running totals for the usd, wall-clock and agent-invocation
    /// dimensions. All three inputs represent an amount consumed by this charge, so
    /// none may be negative.
    /// </summary>
    public void Charge(decimal usd, TimeSpan elapsed, int agentInvocations)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usd);
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time must not be negative.");
        ArgumentOutOfRangeException.ThrowIfNegative(agentInvocations);

        SpentUsd += usd;
        ElapsedWallClock += elapsed;
        AgentInvocations += agentInvocations;
    }

    /// <summary>
    /// Records one correction attempt (e.g. a round of PR review feedback). This
    /// accumulates for the life of the task - it is never reset by a new feedback
    /// cycle - so that repeated review comments cannot let an agent loop indefinitely.
    /// </summary>
    public void RecordCorrectionAttempt() => CorrectionAttempts++;

    public BudgetRemaining Remaining() => new(
        Limits.MaxUsd - SpentUsd,
        Limits.MaxWallClock - ElapsedWallClock,
        Limits.MaxAgentInvocations - AgentInvocations,
        Limits.MaxCorrectionAttempts - CorrectionAttempts);

    /// <summary>
    /// Throws <see cref="BudgetExceededException"/> naming the first exhausted
    /// dimension, checked in this fixed order: Usd, WallClock, AgentInvocations,
    /// CorrectionAttempts. Spending exactly up to a limit is allowed; only spend
    /// strictly beyond it counts as exhausted.
    /// </summary>
    public void EnsureWithinLimits()
    {
        if (SpentUsd > Limits.MaxUsd)
            throw new BudgetExceededException(BudgetDimension.Usd);
        if (ElapsedWallClock > Limits.MaxWallClock)
            throw new BudgetExceededException(BudgetDimension.WallClock);
        if (AgentInvocations > Limits.MaxAgentInvocations)
            throw new BudgetExceededException(BudgetDimension.AgentInvocations);
        if (CorrectionAttempts > Limits.MaxCorrectionAttempts)
            throw new BudgetExceededException(BudgetDimension.CorrectionAttempts);
    }
}
