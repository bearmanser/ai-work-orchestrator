using Orchestrator.Application.Gateway;
using Orchestrator.Application.Policy;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Operations;
using Shouldly;

namespace Orchestrator.Application.Tests;

/// <summary>
/// Covers <see cref="IToolCallStore.RecordIntentAsync"/>'s reuse-vs-refuse decision
/// for a repeat call under the same idempotency key. One row per key is load-bearing:
/// two rows for the same logical operation would make "is this still in flight?"
/// ambiguous, which is exactly what the retry gate depends on being answerable.
/// </summary>
public class InMemoryToolCallStoreRetryTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid RepoA = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static AuthorizationEnvelope Envelope() => new(
        TaskId,
        "requester@kameleongruppen.no",
        [new RepositoryScope(RepoA, ["main"], ["*"], RepositoryAccess.Write)],
        ["staging"], [], [], [], [], [], [], [],
        new BudgetLimits(100m, TimeSpan.FromHours(1), 10, 10),
        Now.AddHours(1), Now.AddMinutes(-1));

    private static BudgetLedger Ledger() => new(TaskId, new BudgetLimits(100m, TimeSpan.FromHours(1), 10, 10));

    private static ToolCall Call(string idempotencyKey) => new(
        TaskId, Guid.CreateVersion7(), "requester@kameleongruppen.no", ActionKind.PullRequestCreate,
        ResourceScope.Repository(RepoA, "main"), $"op-{idempotencyKey}", idempotencyKey,
        "test call", Envelope(), Ledger());

    [Fact]
    public async Task RecordIntentAsync_AfterAFailedResolution_ReusesTheSameRowAndIncrementsAttemptCount()
    {
        var store = new InMemoryToolCallStore();
        var first = await store.RecordIntentAsync(Call("key-1"), new PolicyDecision.Permitted("test"), Now, default);
        await store.ResolveAsync(first.Id, ToolCallOutcome.Failed, Now.AddMinutes(1), default);

        var second = await store.RecordIntentAsync(
            Call("key-1"), new PolicyDecision.Permitted("test"), Now.AddMinutes(2), default);

        second.Id.ShouldBe(first.Id);
        second.AttemptCount.ShouldBe(2);
        second.Outcome.ShouldBe(ToolCallOutcome.Unknown);
    }

    [Fact]
    public async Task RecordIntentAsync_AfterReusingAFailedRow_TheRetryGateBehavesCorrectlyForTheNewAttempt()
    {
        var store = new InMemoryToolCallStore();
        var first = await store.RecordIntentAsync(Call("key-2"), new PolicyDecision.Permitted("test"), Now, default);
        await store.ResolveAsync(first.Id, ToolCallOutcome.Failed, Now.AddMinutes(1), default);

        await store.RecordIntentAsync(Call("key-2"), new PolicyDecision.Permitted("test"), Now.AddMinutes(2), default);

        // The new attempt is Unknown again - the gate must close for it, same as
        // for any other in-flight operation.
        (await store.HasUnknownOutcomesAsync(TaskId, default)).ShouldBeTrue();
    }

    [Fact]
    public async Task RecordIntentAsync_WhileStillUnknown_DoesNotSilentlyReset_TheGateHolds()
    {
        var store = new InMemoryToolCallStore();
        await store.RecordIntentAsync(Call("key-3"), new PolicyDecision.Permitted("test"), Now, default);

        // A second RecordIntentAsync call for the same key while the first attempt
        // is still unresolved must refuse - not quietly reset an in-flight record,
        // which would defeat the entire point of the gate.
        await Should.ThrowAsync<UnreconciledOperationException>(
            () => store.RecordIntentAsync(Call("key-3"), new PolicyDecision.Permitted("test"), Now.AddMinutes(1), default));

        (await store.HasUnknownOutcomesAsync(TaskId, default)).ShouldBeTrue();
    }

    [Fact]
    public async Task RecordIntentAsync_AfterASucceededResolution_RefusesToRetry()
    {
        // Re-running a confirmed non-idempotent success is not obviously safe for
        // any action this codebase records intent for, so it is refused rather
        // than silently redone.
        var store = new InMemoryToolCallStore();
        var first = await store.RecordIntentAsync(Call("key-4"), new PolicyDecision.Permitted("test"), Now, default);
        await store.RecordSuccessAsync(first.Id, "result", Now.AddMinutes(1), default);

        await Should.ThrowAsync<OperationAlreadySucceededException>(
            () => store.RecordIntentAsync(Call("key-4"), new PolicyDecision.Permitted("test"), Now.AddMinutes(2), default));

        // The original, successful row is untouched.
        var record = await store.FindByIdempotencyKeyAsync("key-4", default);
        record!.Id.ShouldBe(first.Id);
        record.Outcome.ShouldBe(ToolCallOutcome.Succeeded);
        record.AttemptCount.ShouldBe(1);
    }
}
