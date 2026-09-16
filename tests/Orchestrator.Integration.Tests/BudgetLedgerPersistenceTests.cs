using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Budgets;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class BudgetLedgerPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SaveLedger_RoundTrips_AllEightValuesThroughAFreshDbContext()
    {
        var taskId = Guid.CreateVersion7();
        var limits = new BudgetLimits(
            MaxUsd: 12.345678m,
            MaxWallClock: TimeSpan.FromMinutes(90) + TimeSpan.FromMilliseconds(250),
            MaxAgentInvocations: 8,
            MaxCorrectionAttempts: 3);

        var created = new BudgetLedger(taskId, limits);
        created.Charge(
            usd: 4.567891m,
            elapsed: TimeSpan.FromMinutes(20) + TimeSpan.FromMilliseconds(125),
            agentInvocations: 2);
        created.RecordCorrectionAttempt();

        await using (var db = fixture.NewDbContext())
        {
            db.Budgets.Add(created);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.NewDbContext();
        var loaded = await read.Budgets.SingleAsync(l => l.Id == created.Id);

        loaded.TaskId.ShouldBe(taskId);

        loaded.Limits.MaxUsd.ShouldBe(limits.MaxUsd);
        loaded.Limits.MaxWallClock.ShouldBe(limits.MaxWallClock);
        loaded.Limits.MaxAgentInvocations.ShouldBe(limits.MaxAgentInvocations);
        loaded.Limits.MaxCorrectionAttempts.ShouldBe(limits.MaxCorrectionAttempts);

        loaded.SpentUsd.ShouldBe(4.567891m);
        loaded.ElapsedWallClock.ShouldBe(TimeSpan.FromMinutes(20) + TimeSpan.FromMilliseconds(125));
        loaded.AgentInvocations.ShouldBe(2);
        loaded.CorrectionAttempts.ShouldBe(1);
    }
}
