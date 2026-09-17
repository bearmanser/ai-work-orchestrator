using Orchestrator.Application.Gateway;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Policy;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Operations;
using Shouldly;

namespace Orchestrator.Application.Tests;

/// <summary>
/// Covers the three paths reconciliation can take for one Unknown record: a
/// registered reconciler resolving it to Succeeded, one resolving it to Failed, and
/// - the one that matters most - no reconciler registered at all, which must leave
/// the record Unknown and the retry gate closed.
/// </summary>
public class OperationReconciliationServiceTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid RepoA = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly ManualTimeProvider _clock = new(Now);

    private static AuthorizationEnvelope Envelope() => new(
        TaskId,
        "requester@kameleongruppen.no",
        [new RepositoryScope(RepoA, ["main"], ["*"], RepositoryAccess.Write)],
        ["staging"], [], [], [], [], [], [], [],
        new BudgetLimits(100m, TimeSpan.FromHours(1), 10, 10),
        Now.AddHours(1), Now.AddMinutes(-1));

    private static BudgetLedger Ledger() => new(TaskId, new BudgetLimits(100m, TimeSpan.FromHours(1), 10, 10));

    private static ToolCall Call(ActionKind action, string idempotencyKey) => new(
        TaskId, Guid.CreateVersion7(), "requester@kameleongruppen.no", action,
        ResourceScope.Repository(RepoA, "main"), $"op-{idempotencyKey}", idempotencyKey,
        "test call", Envelope(), Ledger());

    private async Task<InMemoryToolCallStore> StoreWithUnknownRecordAsync(ActionKind action, string idempotencyKey)
    {
        var store = new InMemoryToolCallStore();
        await store.RecordIntentAsync(
            Call(action, idempotencyKey), new PolicyDecision.Permitted("test"), Now, default);
        return store;
    }

    [Fact]
    public async Task ReconcileAsync_RegisteredReconcilerResolvesSucceeded_RecordBecomesSucceeded()
    {
        var store = await StoreWithUnknownRecordAsync(ActionKind.PullRequestCreate, "key-succeeded");
        var reconciler = new FakeReconciler(ActionKind.PullRequestCreate, ReconciliationOutcome.Succeeded);
        var service = new OperationReconciliationService(store, [reconciler], _clock);

        var summary = await service.ReconcileAsync(TaskId, default);

        summary.Resolved.ShouldBe(1);
        summary.Failed.ShouldBe(0);
        summary.StillUnknown.ShouldBe(0);
        summary.FullyReconciled.ShouldBeTrue();
        (await store.HasUnknownOutcomesAsync(TaskId, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task ReconcileAsync_RegisteredReconcilerResolvesFailed_RecordBecomesFailedNotUnknown()
    {
        var store = await StoreWithUnknownRecordAsync(ActionKind.PullRequestCreate, "key-failed");
        var reconciler = new FakeReconciler(ActionKind.PullRequestCreate, ReconciliationOutcome.Failed);
        var service = new OperationReconciliationService(store, [reconciler], _clock);

        var summary = await service.ReconcileAsync(TaskId, default);

        summary.Resolved.ShouldBe(0);
        summary.Failed.ShouldBe(1);
        summary.StillUnknown.ShouldBe(0);
        summary.FullyReconciled.ShouldBeTrue();
        (await store.HasUnknownOutcomesAsync(TaskId, default)).ShouldBeFalse();

        var record = await store.FindByIdempotencyKeyAsync("key-failed", default);
        record!.Outcome.ShouldBe(ToolCallOutcome.Failed);
    }

    [Fact]
    public async Task ReconcileAsync_NoReconcilerRegisteredForTarget_LeavesRecordUnknownAndGateClosed()
    {
        // This is the path that matters: nobody checked, so nothing may be assumed.
        var store = await StoreWithUnknownRecordAsync(ActionKind.RepositoryWrite, "key-unresolved");
        var service = new OperationReconciliationService(store, [], _clock);

        var summary = await service.ReconcileAsync(TaskId, default);

        summary.Resolved.ShouldBe(0);
        summary.Failed.ShouldBe(0);
        summary.StillUnknown.ShouldBe(1);
        summary.FullyReconciled.ShouldBeFalse();
        summary.UnresolvedTargets.ShouldContain(ActionKind.RepositoryWrite);

        (await store.HasUnknownOutcomesAsync(TaskId, default)).ShouldBeTrue();
        var record = await store.FindByIdempotencyKeyAsync("key-unresolved", default);
        record!.Outcome.ShouldBe(ToolCallOutcome.Unknown);
    }

    [Fact]
    public async Task ReconcileAsync_ReconcilerReturnsIndeterminate_LeavesRecordUnknown()
    {
        var store = await StoreWithUnknownRecordAsync(ActionKind.PullRequestCreate, "key-indeterminate");
        var reconciler = new FakeReconciler(ActionKind.PullRequestCreate, ReconciliationOutcome.Indeterminate);
        var service = new OperationReconciliationService(store, [reconciler], _clock);

        var summary = await service.ReconcileAsync(TaskId, default);

        summary.StillUnknown.ShouldBe(1);
        summary.FullyReconciled.ShouldBeFalse();
        (await store.HasUnknownOutcomesAsync(TaskId, default)).ShouldBeTrue();
    }

    private sealed class FakeReconciler(ActionKind target, ReconciliationOutcome outcome) : IOperationReconciler
    {
        public ActionKind Target { get; } = target;

        public Task<ReconciliationOutcome> ReconcileAsync(ToolCallRecord record, CancellationToken ct) =>
            Task.FromResult(outcome);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
