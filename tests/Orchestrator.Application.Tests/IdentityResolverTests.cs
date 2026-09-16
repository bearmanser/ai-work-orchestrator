using Orchestrator.Application.Identity;
using Orchestrator.Domain.Identity;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class IdentityResolverTests
{
    private readonly IdentityResolver Resolver;

    public IdentityResolverTests()
    {
        var store = new InMemoryPersonStore();
        store.AddAsync(
            new PersonIdentity(
                Guid.CreateVersion7(),
                "Magnus",
                "U-magnus",
                "magnus-ado",
                "magnus@example.com",
                true),
            default).GetAwaiter().GetResult();
        store.AddAsync(
            new PersonIdentity(
                Guid.CreateVersion7(),
                "Former Employee",
                "U-former-employee",
                "former-ado",
                "former@example.com",
                false),
            default).GetAwaiter().GetResult();
        Resolver = new IdentityResolver(store);
    }

    [Fact]
    public async Task ResolveBySlackUser_MappedUser_ReturnsIdentity() =>
        (await Resolver.ResolveBySlackUserAsync("U-magnus", default))!.DisplayName.ShouldBe("Magnus");

    [Fact]
    public async Task ResolveBySlackUser_UnmappedUser_ReturnsNull() =>
        (await Resolver.ResolveBySlackUserAsync("U-stranger", default)).ShouldBeNull();

    [Fact]
    public async Task ResolveBySlackUser_DeactivatedPerson_ReturnsNull() =>
        (await Resolver.ResolveBySlackUserAsync("U-former-employee", default)).ShouldBeNull();

    [Fact]
    public Task ResolveByDisplayName_IsNotAvailable()
    {
        // Guessing identity from a first name is explicitly out of scope.
        typeof(IdentityResolver).GetMethods()
            .ShouldNotContain(m => m.Name.Contains("ByName", StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}
