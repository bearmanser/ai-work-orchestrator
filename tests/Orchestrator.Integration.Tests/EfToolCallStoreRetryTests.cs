using Orchestrator.Application.Gateway;
using Orchestrator.Application.Policy;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Operations;
using Orchestrator.Infrastructure.Operations;
using Shouldly;

namespace Orchestrator.Integration.Tests;

/// <summary>
/// Proves the fix for the unique index on <c>tool_call_records.idempotency_key</c>:
/// a retry after a Failed resolution must reuse that row, not attempt a second
/// insert under the same key (which the index would reject as a Postgres unique
/// violation - exactly the bug this class exists to catch regressing).
/// </summary>
[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class EfToolCallStoreRetryTests(PostgresFixture fixture)
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
    public async Task RecordIntentAsync_AfterAFailedResolution_ReusesTheRowInsteadOfViolatingTheUniqueIndex()
    {
        await fixture.ResetAsync();

        await using var writeDb = fixture.NewDbContext();
        var store = new EfToolCallStore(writeDb);
        var first = await store.RecordIntentAsync(
            Call("idem-retry-reuse"), new PolicyDecision.Permitted("test"), Now, default);
        await store.ResolveAsync(first.Id, ToolCallOutcome.Failed, Now.AddMinutes(1), default);

        // Through a fresh DbContext/store, as the real retry path would use.
        await using var retryDb = fixture.NewDbContext();
        var retryStore = new EfToolCallStore(retryDb);

        var second = await retryStore.RecordIntentAsync(
            Call("idem-retry-reuse"), new PolicyDecision.Permitted("test"), Now.AddMinutes(2), default);

        second.Id.ShouldBe(first.Id);
        second.AttemptCount.ShouldBe(2);
        second.Outcome.ShouldBe(ToolCallOutcome.Unknown);

        await using var verifyDb = fixture.NewDbContext();
        (await new EfToolCallStore(verifyDb).HasUnknownOutcomesAsync(TaskId, default)).ShouldBeTrue();
    }

    [Fact]
    public async Task RecordIntentAsync_WhileStillUnknown_RefusesRatherThanReset()
    {
        await fixture.ResetAsync();

        await using var writeDb = fixture.NewDbContext();
        var store = new EfToolCallStore(writeDb);
        await store.RecordIntentAsync(Call("idem-still-unknown"), new PolicyDecision.Permitted("test"), Now, default);

        await using var secondDb = fixture.NewDbContext();
        var secondStore = new EfToolCallStore(secondDb);
        await Should.ThrowAsync<UnreconciledOperationException>(() => secondStore.RecordIntentAsync(
            Call("idem-still-unknown"), new PolicyDecision.Permitted("test"), Now.AddMinutes(1), default));
    }

    [Fact]
    public async Task RecordIntentAsync_AfterASucceededResolution_Refuses()
    {
        await fixture.ResetAsync();

        await using var writeDb = fixture.NewDbContext();
        var store = new EfToolCallStore(writeDb);
        var first = await store.RecordIntentAsync(
            Call("idem-succeeded"), new PolicyDecision.Permitted("test"), Now, default);
        await store.RecordSuccessAsync(first.Id, "ok", Now.AddMinutes(1), default);

        await using var secondDb = fixture.NewDbContext();
        var secondStore = new EfToolCallStore(secondDb);
        await Should.ThrowAsync<OperationAlreadySucceededException>(() => secondStore.RecordIntentAsync(
            Call("idem-succeeded"), new PolicyDecision.Permitted("test"), Now.AddMinutes(2), default));
    }
}
