using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Inbox;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class EventRegistryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TryRegister_FirstDelivery_ReturnsTrue()
    {
        await using var db = fixture.NewDbContext();
        var registry = new EventRegistry(db, TimeProvider.System);
        var id = $"evt-{Guid.NewGuid()}";

        var result = await registry.TryRegisterAsync("slack", id, "hash1", default);

        result.IsFirstDelivery.ShouldBeTrue();
    }

    [Fact]
    public async Task TryRegister_SameEventTwice_SecondIsNotFirstDelivery()
    {
        var id = $"evt-{Guid.NewGuid()}";

        EventRegistration first;
        await using (var db1 = fixture.NewDbContext())
        {
            first = await new EventRegistry(db1, TimeProvider.System)
                .TryRegisterAsync("slack", id, "hash1", default);
        }

        EventRegistration second;
        await using (var db2 = fixture.NewDbContext())
        {
            second = await new EventRegistry(db2, TimeProvider.System)
                .TryRegisterAsync("slack", id, "hash1", default);
        }

        second.IsFirstDelivery.ShouldBeFalse();
        second.EventId.ShouldBe(first.EventId);
    }

    [Fact]
    public async Task TryRegister_SameIdDifferentSource_BothAreFirstDelivery()
    {
        var id = $"evt-{Guid.NewGuid()}";

        EventRegistration a;
        await using (var db1 = fixture.NewDbContext())
        {
            a = await new EventRegistry(db1, TimeProvider.System)
                .TryRegisterAsync("slack", id, "h", default);
        }

        EventRegistration b;
        await using (var db2 = fixture.NewDbContext())
        {
            b = await new EventRegistry(db2, TimeProvider.System)
                .TryRegisterAsync("azuredevops", id, "h", default);
        }

        a.IsFirstDelivery.ShouldBeTrue();
        b.IsFirstDelivery.ShouldBeTrue();
    }

    [Fact]
    public async Task TryRegister_ConcurrentIdenticalDeliveries_OnlyOneWins()
    {
        var id = $"evt-{Guid.NewGuid()}";
        // Each concurrent task owns its own DbContext and disposes it after the call.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = fixture.NewDbContext();
            return await new EventRegistry(db, TimeProvider.System)
                .TryRegisterAsync("slack", id, "h", default);
        }));

        results.Count(r => r.IsFirstDelivery).ShouldBe(1);
    }

    [Fact]
    public async Task ResetAsync_TruncatesInboundEvents()
    {
        var id = $"evt-{Guid.NewGuid()}";

        EventRegistration first;
        await using (var db1 = fixture.NewDbContext())
        {
            first = await new EventRegistry(db1, TimeProvider.System)
                .TryRegisterAsync("slack", id, "hash1", default);
        }
        first.IsFirstDelivery.ShouldBeTrue();

        await fixture.ResetAsync();

        EventRegistration afterReset;
        await using (var db2 = fixture.NewDbContext())
        {
            afterReset = await new EventRegistry(db2, TimeProvider.System)
                .TryRegisterAsync("slack", id, "hash1", default);
        }
        afterReset.IsFirstDelivery.ShouldBeTrue();
    }
}
