using Orchestrator.Domain.Budgets;
using Shouldly;

namespace Orchestrator.Domain.Tests;

public class BudgetLedgerTests
{
    private static readonly BudgetLimits Limits = new(
        MaxUsd: 12.00m, MaxWallClock: TimeSpan.FromHours(4),
        MaxAgentInvocations: 8, MaxCorrectionAttempts: 3);

    [Fact]
    public void Charge_WithinAllLimits_DoesNotThrow()
    {
        var ledger = new BudgetLedger(Guid.CreateVersion7(), Limits);
        ledger.Charge(3.50m, TimeSpan.FromMinutes(20), agentInvocations: 1);
        Should.NotThrow(ledger.EnsureWithinLimits);
    }

    [Fact]
    public void Charge_ExceedingSpend_ThrowsNamingTheDimension()
    {
        var ledger = new BudgetLedger(Guid.CreateVersion7(), Limits);
        ledger.Charge(12.01m, TimeSpan.Zero, 1);
        var ex = Should.Throw<BudgetExceededException>(ledger.EnsureWithinLimits);
        ex.Dimension.ShouldBe(BudgetDimension.Usd);
    }

    [Fact]
    public void CorrectionAttempts_AccumulateAcrossFeedbackCycles()
    {
        var ledger = new BudgetLedger(Guid.CreateVersion7(), Limits);
        ledger.RecordCorrectionAttempt();
        ledger.RecordCorrectionAttempt();
        Should.NotThrow(ledger.EnsureWithinLimits);

        ledger.RecordCorrectionAttempt();
        ledger.RecordCorrectionAttempt();
        Should.Throw<BudgetExceededException>(ledger.EnsureWithinLimits)
              .Dimension.ShouldBe(BudgetDimension.CorrectionAttempts);
    }

    [Fact]
    public void Remaining_ReportsEachDimension()
    {
        var ledger = new BudgetLedger(Guid.CreateVersion7(), Limits);
        ledger.Charge(2.00m, TimeSpan.FromHours(1), 2);

        var remaining = ledger.Remaining();
        remaining.Usd.ShouldBe(10.00m);
        remaining.WallClock.ShouldBe(TimeSpan.FromHours(3));
        remaining.AgentInvocations.ShouldBe(6);
        remaining.CorrectionAttempts.ShouldBe(3);
    }
}
