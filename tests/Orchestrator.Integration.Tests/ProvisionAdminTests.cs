using Microsoft.EntityFrameworkCore;
using Orchestrator.Cli.Commands;
using Orchestrator.Infrastructure.Identity;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class ProvisionAdminTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Provision_NewLogin_CreatesActiveAccountWithHashedPassword()
    {
        var command = new ProvisionAdminCommand(fixture.NewDbContext(), new Pbkdf2PasswordHasher(), TimeProvider.System);

        await command.ExecuteAsync("magnus", "a-long-enough-passphrase", default);

        await using var db = fixture.NewDbContext();
        var account = await db.AdminAccounts.SingleAsync(a => a.LoginName == "magnus");
        account.IsActive.ShouldBeTrue();
        account.PasswordHash.ShouldNotContain("a-long-enough-passphrase");
    }

    [Fact]
    public async Task Provision_ExistingLogin_Throws()
    {
        var command = new ProvisionAdminCommand(fixture.NewDbContext(), new Pbkdf2PasswordHasher(), TimeProvider.System);
        await command.ExecuteAsync("dupe", "a-long-enough-passphrase", default);

        await Should.ThrowAsync<InvalidOperationException>(
            () => new ProvisionAdminCommand(fixture.NewDbContext(), new Pbkdf2PasswordHasher(), TimeProvider.System)
                .ExecuteAsync("dupe", "another-long-passphrase", default));
    }

    [Fact]
    public async Task Provision_ShortPassword_IsRejected()
    {
        var command = new ProvisionAdminCommand(fixture.NewDbContext(), new Pbkdf2PasswordHasher(), TimeProvider.System);
        await Should.ThrowAsync<ArgumentException>(() => command.ExecuteAsync("shorty", "short", default));
    }

    [Fact]
    public async Task Provision_MixedCaseLogin_IsStoredLowercase()
    {
        await new ProvisionAdminCommand(fixture.NewDbContext(), new Pbkdf2PasswordHasher(), TimeProvider.System)
            .ExecuteAsync("Magnus.Grinder", "a-long-enough-passphrase", default);

        await using var db = fixture.NewDbContext();
        (await db.AdminAccounts.AnyAsync(a => a.LoginName == "magnus.grinder")).ShouldBeTrue();
    }
}
