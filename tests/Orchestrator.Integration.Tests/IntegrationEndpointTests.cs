using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Api.Endpoints;
using Orchestrator.Application.Approvals;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Identity;
using Orchestrator.Infrastructure.Audit;
using Orchestrator.Infrastructure.Identity;
using Orchestrator.Infrastructure.Persistence;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationEndpointTests(PostgresFixture fixture, OrchestratorApiFactory factory)
    : IClassFixture<OrchestratorApiFactory>
{
    private const string TestSigningSecret = "s3cr3t-slack-signing-value";
    private string _cookieHeader = "";
    private string _csrfToken = "";

    [Fact]
    public async Task GetIntegrations_ReturnsFingerprintsNotValues()
    {
        await ResetAsync();
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_SLACK_SIGNING_SECRET", TestSigningSecret);
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/admin/integrations");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var statuses = JsonSerializer.Deserialize<List<IntegrationStatus>>(body, JsonWebOptions)!;
        var slack = statuses.Single(s => s.Name == "slack");

        slack.Configured.ShouldBeTrue();
        slack.SecretFingerprint.Length.ShouldBe(8);
        body.ShouldNotContain(TestSigningSecret);
    }

    [Fact]
    public async Task GetIntegrations_ListsAllKnownIntegrations()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var statuses = await client.GetFromJsonAsync<List<IntegrationStatus>>("/api/admin/integrations", JsonWebOptions);

        statuses!.Select(s => s.Name).ShouldBe(
            new[] { "slack", "azuredevops", "anthropic", "openai", "p1", "obsidian" },
            ignoreOrder: true);
    }

    [Fact]
    public async Task PutIntegration_WithASecretValueInTheBody_Returns400()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var response = await PutWithCsrfAsync(client, "/api/admin/integrations/azuredevops",
            new { secretRef = "ghp_abcdefghijklmnopqrstuvwxyz0123456789" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutIntegration_WithASecretReference_IsAcceptedAndAudited()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var response = await PutWithCsrfAsync(client, "/api/admin/integrations/azuredevops",
            new { secretRef = "azuredevops.pat" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var db = fixture.NewDbContext();
        (await db.AuditEvents.AnyAsync(e => e.Action == "integration.configure")).ShouldBeTrue();
    }

    [Fact]
    public async Task PutIntegration_UnknownName_Returns404()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var response = await PutWithCsrfAsync(client, "/api/admin/integrations/does-not-exist",
            new { secretRef = "does-not-exist.key" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TestIntegration_UnreachableEndpoint_ReportsUnhealthyWithoutThrowing()
    {
        await ResetAsync();
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_P1_DEFAULT_CONNECTION", "Host=127.0.0.1;Port=1;Timeout=1;Database=nope;Username=nope;Password=nope");
        var client = await AuthenticatedClientAsync();

        var response = await PostWithCsrfAsync(client, "/api/admin/integrations/p1/test", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var status = await response.Content.ReadFromJsonAsync<IntegrationStatus>(JsonWebOptions);
        status!.Health.ShouldBe("Unhealthy");
        status.Detail.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TestIntegration_WithNoLiveCheckAvailable_ReportsNotImplementedRatherThanFakingSuccess()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var response = await PostWithCsrfAsync(client, "/api/admin/integrations/slack/test", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var status = await response.Content.ReadFromJsonAsync<IntegrationStatus>(JsonWebOptions);
        status!.Health.ShouldBe("NotImplemented");
    }

    [Fact]
    public async Task Integrations_WithoutAuthentication_Returns401()
    {
        await ResetAsync();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/integrations");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PutPolicy_RemovingProductionFromForbiddenEnvironments_Is400()
    {
        // Production is a documented non-goal, not a configurable setting - see
        // PolicyRules.Default's remarks.
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var response = await PutWithCsrfAsync(client, "/api/admin/policy",
            new { forbiddenEnvironments = new[] { "staging" } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutPolicy_AddingAFurtherForbiddenEnvironment_Succeeds()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var response = await PutWithCsrfAsync(client, "/api/admin/policy",
            new { forbiddenEnvironments = new[] { "production", "prod", "staging" } });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var rules = await client.GetFromJsonAsync<PolicyRulesDto>("/api/admin/policy", JsonWebOptions);
        rules!.ForbiddenEnvironments.ShouldContain("staging");
    }

    [Fact]
    public async Task GetPolicy_ReturnsTheDefaultRulesUntilAnAdminSavesAChange()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var rules = await client.GetFromJsonAsync<PolicyRulesDto>("/api/admin/policy", JsonWebOptions);

        rules!.ForbiddenEnvironments.ShouldContain("production");
        rules.ForbiddenEnvironments.ShouldContain("prod");
    }

    [Fact]
    public async Task ChangingApprovers_DoesNotAffectAlreadyPendingApprovals()
    {
        // Approval.RequestedFrom is snapshotted (copied into an array) at request time
        // rather than kept as a live reference - see Approval's constructor - so later
        // changing who is eligible to approve can never retroactively change a pending
        // approval's eligible approvers.
        var store = new InMemoryApprovalStore();
        var audit = new AuditWriter(fixture.NewDbContext(), factory.Clock);
        var service = new ApprovalService(store, audit);
        var requestedFrom = new List<string> { "U-magnus" };

        var approval = await service.RequestAsync(
            Guid.CreateVersion7(), "restricted-action", "hash-1", ActionKind.DomainWrite,
            "some resource", requestedFrom, factory.Clock.GetUtcNow().AddHours(1), default);

        requestedFrom.Clear();
        requestedFrom.Add("U-someone-else");

        approval.RequestedFrom.ShouldBe(new[] { "U-magnus" });
    }

    // ---- Test infrastructure ----

    private static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

    private async Task ResetAsync()
    {
        await fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_SLACK_SIGNING_SECRET", null);
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_P1_DEFAULT_CONNECTION", null);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        await SeedAdminAsync("integrations-magnus", "a-long-enough-passphrase");
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { loginName = "integrations-magnus", password = "a-long-enough-passphrase" });
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var cookies = login.Headers.GetValues("Set-Cookie").ToArray();
        _cookieHeader = string.Join("; ", cookies.Select(c => c.Split(';', 2)[0]));
        var csrfCookie = cookies.FirstOrDefault(c => c.StartsWith("orchestrator_csrf", StringComparison.Ordinal));
        _csrfToken = csrfCookie?.Split('=', 2)[1].Split(';')[0] ?? "";

        client.DefaultRequestHeaders.Add("Cookie", _cookieHeader);
        return client;
    }

    private async Task SeedAdminAsync(string login, string password)
    {
        await using var db = fixture.NewDbContext();
        if (await db.AdminAccounts.AnyAsync(a => a.LoginName == login))
            return;

        db.AdminAccounts.Add(new AdminAccount(login, new Pbkdf2PasswordHasher().Hash(password), factory.Clock.GetUtcNow()));
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> PutWithCsrfAsync(HttpClient client, string path, object? content) =>
        SendWithCsrfAsync(client, HttpMethod.Put, path, content);

    private Task<HttpResponseMessage> PostWithCsrfAsync(HttpClient client, string path, object? content) =>
        SendWithCsrfAsync(client, HttpMethod.Post, path, content);

    private async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object? content)
    {
        using var request = new HttpRequestMessage(method, path);
        if (content is not null)
            request.Content = JsonContent.Create(content, options: JsonWebOptions);
        request.Headers.Add("X-CSRF-Token", _csrfToken);
        return await client.SendAsync(request);
    }
}
