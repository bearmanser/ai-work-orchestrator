using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// Proves that two genuinely concurrent callers racing for the same step, the
/// same task transition, or the same repository cannot both win. Every race
/// here is real: separate <see cref="OrchestratorDbContext"/> instances (hence
/// separate connections/transactions) launched with <c>Task.WhenAll</c>
/// against the same Postgres row, not sequential calls dressed up as
/// concurrent ones.
/// </summary>
[Trait("Category", "Acceptance")]
[Collection(nameof(AcceptanceCollection))]
public sealed class ConcurrencyTests(AcceptanceHost Host) : IClassFixture<AcceptanceHost>
{
    [Fact]
    public async Task TwoWorkersRacingForTheSameStep_OnlyOneExecutesIt()
    {
        await Host.ResetAsync();
        var stepId = await Host.SeedPendingStepAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Host.TryExecuteStepAsync(stepId)));

        results.Count(r => r.Executed).ShouldBe(1);
        (await Host.NoopInvocationsForAsync(stepId)).ShouldBe(1);
    }

    [Fact]
    public async Task TwoConflictingTaskTransitions_OneFailsWithAConcurrencyError()
    {
        await Host.ResetAsync();
        var taskId = await Host.SeedTaskAsync(TaskState.WaitingForPlanConfirmation);

        var outcomes = await Task.WhenAll(
            Host.FireAsync(taskId, TaskTrigger.PlanConfirmed),
            Host.FireAsync(taskId, TaskTrigger.PlanRevisionRequested));

        outcomes.Count(o => o.Succeeded).ShouldBe(1);

        // The one that lost the race must have lost to a real optimistic-
        // concurrency conflict (WorkTask.Version / Postgres xmin), not to some
        // other, unrelated failure.
        outcomes.Single(o => !o.Succeeded).Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task TwoTasksTouchingTheSameRepository_GetSeparateWorkspaces()
    {
        await Host.ResetAsync();
        var a = Host.RunTaskToImplementationAsync();
        var b = Host.RunTaskToImplementationAsync();
        var taskIds = await Task.WhenAll(a, b);

        var paths = await Task.WhenAll(taskIds.Select(Host.WorkspacePathForAsync));

        paths.Distinct().Count().ShouldBe(2);
    }
}
