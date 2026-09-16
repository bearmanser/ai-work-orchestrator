using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Audit;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Identity;
using Orchestrator.Infrastructure.Persistence;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class TaskQueryEndpointTests(PostgresFixture fixture, OrchestratorApiFactory factory)
    : IClassFixture<OrchestratorApiFactory>
{
    private readonly PostgresFixture _fixture = fixture;
    private readonly OrchestratorApiFactory _factory = factory;
    private string? _authCookie;
    private string? _nonAdminCookie;

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        LoginAsAdminAsync(client).GetAwaiter().GetResult();
        return client;
    }

    private HttpClient CreateUnauthenticatedClient() => _factory.CreateClient();

    private HttpClient CreateNonAdminClient()
    {
        var client = _factory.CreateClient();
        LoginAsNonAdminAsync(client).GetAwaiter().GetResult();
        return client;
    }

    private async Task LoginAsAdminAsync(HttpClient client)
    {
        await SeedAdminAsync("task-query-admin", "a-long-enough-passphrase");
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { loginName = "task-query-admin", password = "a-long-enough-passphrase" });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _authCookie = CookieHeader(response);
    }

    private async Task LoginAsNonAdminAsync(HttpClient client)
    {
        await SeedAdminAsync("task-query-nonadmin", "a-long-enough-passphrase", isAdmin: false);
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { loginName = "task-query-nonadmin", password = "a-long-enough-passphrase" });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _nonAdminCookie = CookieHeader(response);
    }

    private async Task SeedAdminAsync(string login, string password, bool active = true, bool isAdmin = true)
    {
        await using var db = _fixture.NewDbContext();
        if (await db.AdminAccounts.AnyAsync(a => a.LoginName == login))
            return;

        var account = new AdminAccount(
            login, new Pbkdf2PasswordHasher().Hash(password), _factory.Clock.GetUtcNow());
        if (!active)
            account.Deactivate();
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync();
    }

    private static string CookieHeader(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';', 2)[0]));

    private HttpRequestMessage CreateGetRequest(string url, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        return request;
    }

    private async Task<T?> GetFromJsonAsync<T>(HttpClient client, string url, string? cookie = null)
    {
        using var request = CreateGetRequest(url, cookie);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    private async Task<string> GetStringAsync(HttpClient client, string url, string? cookie = null)
    {
        using var request = CreateGetRequest(url, cookie);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string? cookie = null)
    {
        using var request = CreateGetRequest(url, cookie);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task GetTasks_FiltersByState()
    {
        await _fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = _fixture.ConnectionString;
        await SeedTasksAsync(planning: 2, watchingPullRequests: 1);
        var client = CreateAuthenticatedClient();
        var page = await GetFromJsonAsync<PagedResult<TaskSummary>>(client, "/api/admin/tasks?state=Planning", _authCookie);

        page!.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetTask_WaitingForHuman_ReportsWhoItIsWaitingFor()
    {
        await _fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = _fixture.ConnectionString;
        var taskId = await SeedTaskWaitingForAsync("Peer");
        var client = CreateAuthenticatedClient();
        var detail = await GetFromJsonAsync<TaskDetail>(client, $"/api/admin/tasks/{taskId}", _authCookie);

        detail!.WaitingFor.ShouldBe("Peer");
        detail.BlockedOn.ShouldNotBeNull();
        detail.BlockedOn.ShouldContain("question");
    }

    [Fact]
    public async Task GetTask_SeparatesRuntimeChecksFromDomainChecks()
    {
        await _fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = _fixture.ConnectionString;
        var taskId = await SeedTaskWithEvidenceAsync();
        var client = CreateAuthenticatedClient();
        var detail = await GetFromJsonAsync<TaskDetail>(client, $"/api/admin/tasks/{taskId}", _authCookie);

        detail!.Checks.ShouldNotBeEmpty();
        detail.DomainChecks.ShouldNotBeEmpty();
        typeof(TaskDetail).GetProperty("AllChecks").ShouldBeNull(); // no merged view exists
    }

    [Fact]
    public async Task GetTask_IncludesTheAuditTrail()
    {
        await _fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = _fixture.ConnectionString;
        var taskId = await SeedTaskWithPolicyDenialAsync();
        var client = CreateAuthenticatedClient();
        var detail = await GetFromJsonAsync<TaskDetail>(client, $"/api/admin/tasks/{taskId}", _authCookie);

        detail!.Audit.ShouldContain(e => e.Action == "policy.evaluate" && e.Outcome == "Denied");
    }

    [Fact]
    public async Task GetTask_WithoutASession_Returns401()
    {
        await _fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = _fixture.ConnectionString;
        var client = CreateUnauthenticatedClient();
        (await GetAsync(client, $"/api/admin/tasks/{Guid.CreateVersion7()}"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetArtifact_WithoutAuthentication_Returns401()
    {
        await _fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = _fixture.ConnectionString;
        var client = CreateUnauthenticatedClient();
        (await GetAsync(client, $"/api/admin/tasks/{Guid.CreateVersion7()}/artifacts/{Guid.CreateVersion7().ToString("N")}"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetArtifact_WithAuthentication_ButMissingFile_Returns404()
    {
        await _fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = _fixture.ConnectionString;
        var client = CreateAuthenticatedClient();
        (await GetAsync(client, $"/api/admin/tasks/{Guid.CreateVersion7()}/artifacts/{Guid.CreateVersion7().ToString("N")}", _authCookie))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetTask_NeverReturnsASecretValue()
    {
        await _fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = _fixture.ConnectionString;
        var taskId = await SeedTaskWithIntegrationConfigAsync();
        var client = CreateAuthenticatedClient();
        var json = await GetStringAsync(client, $"/api/admin/tasks/{taskId}", _authCookie);
        json.ShouldNotContain("TestPat");
    }

    private async Task SeedTasksAsync(int planning, int watchingPullRequests)
    {
        await using var db = _fixture.NewDbContext();
        var clock = _factory.Clock;
        var now = clock.GetUtcNow();

        for (int i = 0; i < planning; i++)
        {
            var task = new WorkTask($"Planning task {i}", "U1", TaskSource.Slack, $"c-plan-{Guid.NewGuid()}", now);
            task.Fire(TaskTrigger.PlanningStarted, now);
            db.Tasks.Add(task);
        }

        for (int i = 0; i < watchingPullRequests; i++)
        {
            var task = new WorkTask($"Watching PR task {i}", "U1", TaskSource.Slack, $"c-wpr-{Guid.NewGuid()}", now);
            task.Fire(TaskTrigger.PlanningStarted, now);
            task.Fire(TaskTrigger.PlanPosted, now);
            task.Fire(TaskTrigger.PlanConfirmed, now);
            task.Fire(TaskTrigger.WorkDispatched, now);
            task.Fire(TaskTrigger.ImplementationFinished, now);
            task.Fire(TaskTrigger.PullRequestOpened, now);
            db.Tasks.Add(task);
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTaskWaitingForAsync(string peerName)
    {
        await using var db = _fixture.NewDbContext();
        var clock = _factory.Clock;
        var now = clock.GetUtcNow();

        var task = new WorkTask("Waiting for human", "U1", TaskSource.Slack, $"c-wait-{Guid.NewGuid()}", now);
        task.Fire(TaskTrigger.PlanningStarted, now);
        task.Fire(TaskTrigger.BlockingQuestionRaised, now);
        db.Tasks.Add(task);

        var step = new Step(task.Id, "implementation", null, null, now);
        db.Steps.Add(step);

        var question = new HumanQuestion(
            task.Id, step.Id, Guid.NewGuid(), "Please review this question",
            "C123", "1234567890.123456", "1234567890.123456",
            now, now.AddMinutes(5), now.AddMinutes(10), now.AddHours(1));
        db.HumanQuestions.Add(question);

        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<Guid> SeedTaskWithEvidenceAsync()
    {
        await using var db = _fixture.NewDbContext();
        var clock = _factory.Clock;
        var now = clock.GetUtcNow();

        var task = new WorkTask("Task with evidence", "U1", TaskSource.Slack, $"c-evid-{Guid.NewGuid()}", now);
        db.Tasks.Add(task);

        var step = new Step(task.Id, "implementation", null, null, now);
        db.Steps.Add(step);

        // Add audit events for checks - DetailsJson must contain "P1" for domain checks
        db.AuditEvents.Add(new AuditEvent("system", task.Id, "check.run", "Local test execution", "Passed", "corr-1", """{"check": "dotnet test"}""", now));
        db.AuditEvents.Add(new AuditEvent("system", task.Id, "check.run", "P1 validation", "Passed", "corr-2", """{"check": "picked-order-count", "domain": "P1"}""", now));

        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<Guid> SeedTaskWithPolicyDenialAsync()
    {
        await using var db = _fixture.NewDbContext();
        var clock = _factory.Clock;
        var now = clock.GetUtcNow();

        var task = new WorkTask("Task with policy denial", "U1", TaskSource.Slack, $"c-policy-{Guid.NewGuid()}", now);
        db.Tasks.Add(task);

        db.AuditEvents.Add(new AuditEvent("system", task.Id, "policy.evaluate", "Restricted action request", "Denied", "corr-3", "{}", now));

        await db.SaveChangesAsync();
        return task.Id;
    }



    private async Task<Guid> SeedTaskWithIntegrationConfigAsync()
    {
        await using var db = _fixture.NewDbContext();
        var clock = _factory.Clock;
        var now = clock.GetUtcNow();

        var task = new WorkTask("Task with secret config", "U1", TaskSource.Slack, $"c-secret-{Guid.NewGuid()}", now);
        db.Tasks.Add(task);

        // Add an audit event with a secret-like value
        db.AuditEvents.Add(new AuditEvent("system", task.Id, "integration.config", "Config with PAT", "Success", "corr-4",
            """{"pat": "TestPat12345"}""", now));

        await db.SaveChangesAsync();
        return task.Id;
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record TaskSummary(
    Guid Id,
    string Objective,
    TaskState State,
    string RequesterDisplayName,
    DateTimeOffset UpdatedAt,
    string? BlockedOn,
    string? WaitingFor);

public sealed record TaskDetail(
    Guid Id,
    string Objective,
    TaskState State,
    string RequesterDisplayName,
    DateTimeOffset UpdatedAt,
    string? BlockedOn,
    string? WaitingFor,
    IReadOnlyList<StepSummary> Steps,
    IReadOnlyList<PullRequestSummary> PullRequests,
    IReadOnlyList<CheckResultSummary> Checks,
    IReadOnlyList<DomainCheckSummary> DomainChecks,
    PlanRevisionSummary? ConfirmedPlan,
    BudgetSummary Budget,
    IReadOnlyList<AuditEntrySummary> Audit);

public sealed record StepSummary(Guid Id, string Capability, StepStatus Status, DateTimeOffset CreatedAt);

public sealed record PullRequestSummary(int PullRequestId, string Url, bool IsRequired, bool IsApproved, string? ApprovalReason);

public sealed record CheckResultSummary(string Name, string Outcome, DateTimeOffset RunAt);

public sealed record DomainCheckSummary(string Name, string Outcome, DateTimeOffset RunAt);

public sealed record PlanRevisionSummary(int Revision, DateTimeOffset ConfirmedAt);

public sealed record BudgetSummary(
    decimal MaxUsd,
    decimal SpentUsd,
    TimeSpan MaxWallClock,
    TimeSpan ElapsedWallClock,
    int MaxAgentInvocations,
    int AgentInvocations,
    int MaxCorrectionAttempts,
    int CorrectionAttempts);

public sealed record AuditEntrySummary(
    Guid Id,
    string Actor,
    string Action,
    string Reason,
    string Outcome,
    DateTimeOffset OccurredAt);
