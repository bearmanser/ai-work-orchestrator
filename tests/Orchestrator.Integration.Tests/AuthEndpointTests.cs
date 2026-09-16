using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Identity;
using Orchestrator.Infrastructure.Identity;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class AuthEndpointTests(PostgresFixture fixture, OrchestratorApiFactory factory)
    : IClassFixture<OrchestratorApiFactory>
{
    [Fact]
    public async Task Login_ValidCredentials_SetsHttpOnlySessionCookie()
    {
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        await SeedAdminAsync("auth-magnus", "a-long-enough-passphrase");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { loginName = "auth-magnus", password = "a-long-enough-passphrase" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        var session = cookies.Single(c => c.StartsWith("orchestrator_session", StringComparison.Ordinal));
        session.ShouldContain("httponly", Case.Insensitive);
        session.ShouldContain("samesite=strict", Case.Insensitive);
        cookies.ShouldContain(c => c.StartsWith("orchestrator_csrf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401_AndAuditsTheFailure()
    {
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        await SeedAdminAsync("auth-magnus2", "a-long-enough-passphrase");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { loginName = "auth-magnus2", password = "nope" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var db = fixture.NewDbContext();
        (await db.AuditEvents.AnyAsync(e => e.Action == "auth.login" && e.Outcome == "Failed")).ShouldBeTrue();
    }

    [Fact]
    public async Task Login_DeactivatedAccount_Returns401()
    {
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        await SeedAdminAsync("auth-gone", "a-long-enough-passphrase", active: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { loginName = "auth-gone", password = "a-long-enough-passphrase" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/auth/register")]
    [InlineData("/api/auth/signup")]
    [InlineData("/api/admin/accounts")]
    public async Task AccountCreationEndpoints_DoNotExist(string path)
    {
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(path, new { loginName = "x", password = "y" });

        response.StatusCode.ShouldBeOneOf(
            HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_WithoutSession_Returns401()
    {
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        using var client = factory.CreateClient();

        // /api/admin/tasks does not exist yet; repositories is the first real
        // admin endpoint and carries the same AdminOnly policy.
        var response = await client.GetAsync("/api/admin/repositories");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnsafeVerb_WithoutCsrfHeader_Returns403()
    {
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        await SeedAdminAsync("auth-magnus3", "a-long-enough-passphrase");
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { loginName = "auth-magnus3", password = "a-long-enough-passphrase" });
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A real unsafe endpoint: authenticated but missing the CSRF header.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/repositories");
        request.Headers.Add("Cookie", CookieHeader(login));
        // Deliberately no X-CSRF-Token header.

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task SeedAdminAsync(string login, string password, bool active = true)
    {
        await using var db = fixture.NewDbContext();
        if (await db.AdminAccounts.AnyAsync(a => a.LoginName == login))
            return;

        var account = new AdminAccount(
            login, new Pbkdf2PasswordHasher().Hash(password), DateTimeOffset.UtcNow);
        if (!active)
            account.Deactivate();
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync();
    }

    private static string CookieHeader(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';', 2)[0]));
}
