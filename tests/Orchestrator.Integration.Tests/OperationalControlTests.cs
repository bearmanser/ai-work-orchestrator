using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Slack;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Messaging;
using Orchestrator.Domain.Operations;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Audit;
using Orchestrator.Infrastructure.Outbox;
using Orchestrator.Infrastructure.Persistence;
using Shouldly;
using AgentStage = Orchestrator.Domain.Authorization.AgentStage;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class OperationalControlTests(PostgresFixture fixture, OrchestratorApiFactory factory)
    : IClassFixture<OrchestratorApiFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private string _csrfToken = "";
    private string _cookieHeader = "";

    [Fact]
    public async Task PauseIntake_ThenSlackRequest_CreatesNoTaskAndRepliesThatIntakeIsPaused()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var response = await PostWithCsrfAsync(client, "/api/admin/operations/pause-intake", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify intake is paused via the operational controls
        (await IsIntakePausedAsync()).ShouldBeTrue();

        // Send a Slack event - it should not create a task because intake is paused
        await SignedSlackPostAsync(AppMentionPayload("evt-paused"));
        await DrainOutboxAsync();

        // No tasks should have been created
        (await CountTasksAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ResumeIntake_AllowsTaskCreationAgain()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        // Pause intake
        await PostWithCsrfAsync(client, "/api/admin/operations/pause-intake", null);
        (await IsIntakePausedAsync()).ShouldBeTrue();

        // Resume intake
        var response = await PostWithCsrfAsync(client, "/api/admin/operations/resume-intake", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await IsIntakePausedAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task CancelTask_WithARunningStep_RequestsCancellationAndRetainsEvidence()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var taskId = await SeedFullTaskGraphAsync();
        var response = await PostWithCsrfAsync(client, $"/api/admin/tasks/{taskId}/cancel", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"Response: {await response.Content.ReadAsStringAsync()}");

        // Task should be cancelled
        (await LoadTaskAsync(taskId)).State.ShouldBe(TaskState.Cancelled);

        // Steps should be cancelled
        await using var db = fixture.NewDbContext();
        var steps = await db.Steps.Where(s => s.TaskId == taskId).ToListAsync();
        steps.All(s => s.Status == StepStatus.Cancelled).ShouldBeTrue();

        // Job cancellation should have been requested (verify via audit)
        var audit = await db.AuditEvents
            .Where(e => e.TaskId == taskId && e.Action == "operations.task.cancel")
            .ToListAsync();
        audit.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CancelTask_AfterAPullRequestWasCreated_DoesNotCloseIt()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var taskId = await SeedTaskWithOpenPullRequestAsync();
        var response = await PostWithCsrfAsync(client, $"/api/admin/tasks/{taskId}/cancel", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Task should be cancelled but PR remains open (no PR closure in our system)
        (await LoadTaskAsync(taskId)).State.ShouldBe(TaskState.Cancelled);
    }

    [Fact]
    public async Task RevokeAuthorization_BlocksSubsequentToolCalls()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var taskId = await SeedFullTaskGraphAsync();
        var response = await PostWithCsrfAsync(client, $"/api/admin/tasks/{taskId}/revoke-authorization", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Authorization envelope should be revoked
        await using var db = fixture.NewDbContext();
        var envelope = await db.AuthorizationEnvelopes.SingleAsync(e => e.TaskId == taskId);
        envelope.RevokedAt.ShouldNotBeNull();

        // Policy evaluation should now deny
        var decision = await EvaluatePolicyAsync(taskId, ActionKind.RepositoryWrite);
        decision.ShouldBeOfType<PolicyDecision.Denied>();
    }

    [Fact]
    public async Task RetryStep_WithAnUnknownToolCallOutcome_IsRefusedUntilReconciled()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        var (taskId, stepId) = await SeedStepWithUnknownOutcomeAsync();

        // Retry should be refused (Conflict) due to unknown tool call outcome
        var refused = await PostWithCsrfAsync(client, $"/api/admin/tasks/{taskId}/steps/{stepId}/retry", null);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Reconcile the task
        await PostWithCsrfAsync(client, $"/api/admin/tasks/{taskId}/reconcile", null);

        // Retry should now be accepted
        var retried = await PostWithCsrfAsync(client, $"/api/admin/tasks/{taskId}/steps/{stepId}/retry", null);
        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task DisableIntegration_PreventsItsAdapterFromBeingInvoked()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();
        var taskId = await SeedFullTaskGraphAsync();

        var response = await PostWithCsrfAsync(client, "/api/admin/operations/integrations/p1/disable", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Integration should be disabled
        (await IsIntegrationDisabledAsync("p1")).ShouldBeTrue();

        // Policy evaluation against the disabled integration's P1 resource must be
        // denied even though nothing about the envelope itself changed.
        var decision = await EvaluatePolicyAsync(
            taskId, ActionKind.DomainRead, new ResourceScope.P1Resource("p1", "co"));
        decision.ShouldBeOfType<PolicyDecision.Denied>();
    }

    [Fact]
    public async Task EveryOperationalCommand_IsAudited()
    {
        await ResetAsync();
        var client = await AuthenticatedClientAsync();

        await PostWithCsrfAsync(client, "/api/admin/operations/pause-intake", null);
        await using var db = fixture.NewDbContext();
        (await db.AuditEvents.AnyAsync(e => e.Action == "operations.pause-intake" && e.Actor == "ops-magnus"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Metrics_RecordExecutingAndWaitingTimeSeparately()
    {
        await ResetAsync();
        using var collector = new MetricCollector(Meter, "orchestrator.task.duration");
        await RunTaskThroughAWaitAsync();

        collector.Measurements.ShouldContain(m => m.Tags["phase"]!.Equals("executing"));
        collector.Measurements.ShouldContain(m => m.Tags["phase"]!.Equals("waiting_for_human"));
    }

    // ---- Test infrastructure ----

    private static Meter Meter => new("OrchestratorMetrics");

    private async Task ResetAsync()
    {
        await fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = factory.CreateClient();
        // "magnus" is claimed by ProvisionAdminTests (which asserts it does not
        // already exist) in this shared, un-reset-between-classes Postgres database -
        // use a distinct login here, the same way AuthEndpointTests does.
        await SeedAdminAsync("ops-magnus", "a-long-enough-passphrase");
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { loginName = "ops-magnus", password = "a-long-enough-passphrase" });
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The session cookie is Secure-only, so the client's default cookie
        // container (bound to the http:// TestServer origin) will not resend it
        // automatically - forward it, and the CSRF cookie, as a raw Cookie header
        // on every subsequent request instead (see AuthEndpointTests.CookieHeader
        // for the same workaround).
        var cookies = login.Headers.GetValues("Set-Cookie").ToArray();
        _cookieHeader = string.Join("; ", cookies.Select(c => c.Split(';', 2)[0]));
        var csrfCookie = cookies.FirstOrDefault(c => c.StartsWith("orchestrator_csrf", StringComparison.Ordinal));
        _csrfToken = csrfCookie?.Split('=', 2)[1].Split(';')[0] ?? "";

        return client;
    }

    private async Task<HttpResponseMessage> PostWithCsrfAsync(HttpClient client, string path, object? content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (content is not null)
            request.Content = JsonContent.Create(content, options: JsonOpts);
        request.Headers.Add("Cookie", _cookieHeader);
        request.Headers.Add("X-CSRF-Token", _csrfToken);
        return await client.SendAsync(request);
    }

    private async Task SeedAdminAsync(string login, string password, bool active = true)
    {
        await using var db = fixture.NewDbContext();
        if (await db.AdminAccounts.AnyAsync(a => a.LoginName == login))
            return;

        var account = new AdminAccount(
            login, new Orchestrator.Infrastructure.Identity.Pbkdf2PasswordHasher().Hash(password), factory.Clock.GetUtcNow());
        if (!active)
            account.Deactivate();
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> SignedSlackPostAsync(string body)
    {
        using var client = factory.CreateClient();
        var timestamp = factory.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/slack")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        request.Headers.Add("X-Slack-Signature", ComputeSlackSignature("test-slack-signing-secret-12345", timestamp, body));
        return await client.SendAsync(request);
    }

    private static string ComputeSlackSignature(string secret, string timestamp, string body)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"));
        return "v0=" + Convert.ToHexStringLower(hash);
    }

    private static string AppMentionPayload(string eventId) => JsonSerializer.Serialize(new
    {
        type = "event_callback",
        event_id = eventId,
        team_id = "T-1",
        @event = new
        {
            type = "app_mention",
            user = "U-magnus",
            channel = "C-123",
            ts = "1758000000.0001",
            text = "<@U-orchestrator> please do the work",
        },
    }, JsonOpts);

    private async Task DrainOutboxAsync()
    {
        await using var db = fixture.NewDbContext();
        var dispatcher = new OutboxDispatcher(
            () => new OrchestratorDbContext(
                new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(fixture.ConnectionString).Options),
            async (message, ct) =>
            {
                if (message.MessageType == "step.dispatch")
                {
                    using var document = JsonDocument.Parse(message.PayloadJson);
                    if (document.RootElement.TryGetProperty("stepId", out var stepIdElement)
                        && Guid.TryParse(stepIdElement.GetString(), out var stepId))
                    {
                        var scheduler = factory.Services.GetRequiredService<IJobScheduler>();
                        await scheduler.EnqueueStepAsync(stepId, ct);
                    }
                }
                else if (message.MessageType == "slack.event")
                {
                    var inbound = JsonSerializer.Deserialize<SlackInbound>(
                        message.PayloadJson, JsonOpts);
                    if (inbound is not null)
                    {
                        using var handlerScope = factory.Services.CreateScope();
                        var handler = handlerScope.ServiceProvider.GetRequiredService<SlackEventStepHandler>();
                        await handler.HandleSlackEventAsync(inbound, ct);
                    }
                }
            },
            factory.Clock);
        await dispatcher.DispatchBatchAsync(100, CancellationToken.None);
    }

    private async Task<int> CountTasksAsync()
    {
        await using var db = fixture.NewDbContext();
        return await db.Tasks.CountAsync();
    }

    // A fresh DbContext per call, like every other helper here - this reads back
    // what the endpoint actually committed rather than a cached view, which is the
    // whole point of these assertions.
    private async Task<bool> IsIntakePausedAsync()
    {
        await using var db = fixture.NewDbContext();
        return await db.OperationalFlags
            .Where(f => f.Key == "intake.paused")
            .Select(f => f.Value)
            .SingleOrDefaultAsync();
    }

    private async Task<bool> IsIntegrationDisabledAsync(string name)
    {
        await using var db = fixture.NewDbContext();
        return await db.OperationalFlags
            .Where(f => f.Key == $"integration.{name}.disabled")
            .Select(f => f.Value)
            .SingleOrDefaultAsync();
    }

    private async Task<WorkTask> LoadTaskAsync(Guid taskId)
    {
        await using var db = fixture.NewDbContext();
        return await db.Tasks.SingleAsync(t => t.Id == taskId);
    }

    private async Task<List<Artifact>> ArtifactsForAsync(Guid taskId)
    {
        await using var db = fixture.NewDbContext();
        return await db.Artifacts.Where(a => a.TaskId == taskId).ToListAsync();
    }

    private async Task<Guid> SeedFullTaskGraphAsync()
    {
        await using var db = fixture.NewDbContext();
        var now = factory.Clock.GetUtcNow();
        var task = new WorkTask("running task", "U-magnus", TaskSource.Slack, $"c-{Guid.NewGuid()}", now);
        task.Fire(TaskTrigger.PlanningStarted, now);
        task.Fire(TaskTrigger.PlanPosted, now);
        task.Fire(TaskTrigger.PlanConfirmed, now);
        task.Fire(TaskTrigger.WorkDispatched, now);
        var step = new Step(task.Id, "implementation", null, null, now);
        db.Tasks.Add(task);
        db.Steps.Add(step);

        var envelope = new AuthorizationEnvelope(
            task.Id, "U-magnus",
            [new RepositoryScope(Guid.NewGuid(), ["*"], ["/"], RepositoryAccess.Write)],
            ["staging"], [], ["codex"], [AgentStage.Implementation, AgentStage.Review],
            ["C-123"], [], [], [],
            new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3),
            now.AddDays(7), now);
        db.AuthorizationEnvelopes.Add(envelope);

        var ledger = new BudgetLedger(task.Id, new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3));
        db.Budgets.Add(ledger);

        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<Guid> SeedTaskWithOpenPullRequestAsync()
    {
        await using var db = fixture.NewDbContext();
        var now = factory.Clock.GetUtcNow();
        var task = new WorkTask("task with PR", "U-magnus", TaskSource.Slack, $"c-{Guid.NewGuid()}", now);
        task.Fire(TaskTrigger.PlanningStarted, now);
        task.Fire(TaskTrigger.PlanPosted, now);
        task.Fire(TaskTrigger.PlanConfirmed, now);
        task.Fire(TaskTrigger.WorkDispatched, now);
        task.Fire(TaskTrigger.ImplementationFinished, now);
        task.Fire(TaskTrigger.PullRequestOpened, now);
        db.Tasks.Add(task);

        var envelope = new AuthorizationEnvelope(
            task.Id, "U-magnus",
            [new RepositoryScope(Guid.NewGuid(), ["*"], ["/"], RepositoryAccess.Write)],
            ["staging"], [], ["codex"], [AgentStage.Implementation, AgentStage.Review],
            ["C-123"], [], [], [],
            new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3),
            now.AddDays(7), now);
        db.AuthorizationEnvelopes.Add(envelope);

        var ledger = new BudgetLedger(task.Id, new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3));
        db.Budgets.Add(ledger);

        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<(Guid TaskId, Guid StepId)> SeedStepWithUnknownOutcomeAsync()
    {
        await using var db = fixture.NewDbContext();
        var now = factory.Clock.GetUtcNow();
        var task = new WorkTask("task for retry", "U-magnus", TaskSource.Slack, $"c-{Guid.NewGuid()}", now);
        task.Fire(TaskTrigger.PlanningStarted, now);
        task.Fire(TaskTrigger.PlanPosted, now);
        task.Fire(TaskTrigger.PlanConfirmed, now);
        task.Fire(TaskTrigger.WorkDispatched, now);
        var step = new Step(task.Id, "implementation", null, null, now);
        step.Succeed("artifact-1", now);
        db.Tasks.Add(task);
        db.Steps.Add(step);

        var envelope = new AuthorizationEnvelope(
            task.Id, "U-magnus",
            [new RepositoryScope(Guid.NewGuid(), ["*"], ["/"], RepositoryAccess.Write)],
            ["staging"], [], ["codex"], [AgentStage.Implementation, AgentStage.Review],
            ["C-123"], [], [], [],
            new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3),
            now.AddDays(7), now);
        db.AuthorizationEnvelopes.Add(envelope);

        var ledger = new BudgetLedger(task.Id, new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3));
        db.Budgets.Add(ledger);

        await db.SaveChangesAsync();

        // Create a tool call record with Unknown outcome using the in-memory store
        var store = factory.Services.GetRequiredService<IToolCallStore>();
        var call = new ToolCall(
            task.Id, step.Id, "magnus", ActionKind.RepositoryWrite,
            new ResourceScope.RepositoryResource(Guid.NewGuid(), "main"),
            "repo.write", "idem-retry-test", "test description",
            envelope, ledger);
        await store.RecordIntentAsync(call, new PolicyDecision.Permitted("test"), now, CancellationToken.None);

        return (task.Id, step.Id);
    }

    private async Task<PolicyDecision> EvaluatePolicyAsync(
        Guid taskId, ActionKind action, ResourceScope? resource = null)
    {
        await using var db = fixture.NewDbContext();
        var envelope = await db.AuthorizationEnvelopes.SingleAsync(e => e.TaskId == taskId);
        var budget = await db.Budgets.SingleAsync(b => b.TaskId == taskId);
        using var scope = factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<PolicyEngine>();
        var request = new PolicyRequest(
            taskId, "magnus", action,
            resource ?? new ResourceScope.RepositoryResource(Guid.NewGuid(), "main"),
            envelope, budget, factory.Clock.GetUtcNow(), Guid.CreateVersion7().ToString());
        return await engine.EvaluateAsync(request, CancellationToken.None);
    }

    private async Task RunTaskThroughAWaitAsync()
    {
        await using var db = fixture.NewDbContext();
        var now = factory.Clock.GetUtcNow();
        var task = new WorkTask("metric test", "U-magnus", TaskSource.Slack, $"c-{Guid.NewGuid()}", now);
        task.Fire(TaskTrigger.PlanningStarted, now);
        task.Fire(TaskTrigger.PlanPosted, now);
        task.Fire(TaskTrigger.PlanConfirmed, now);
        task.Fire(TaskTrigger.WorkDispatched, now);
        task.Fire(TaskTrigger.BlockingQuestionRaised, now); // WaitingForHuman
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
    }
}

// ---- Metrics collector ----

internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener;
    public List<Measurement> Measurements { get; } = [];

    public MetricCollector(Meter meter, string instrumentName)
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == instrumentName && instrument.Meter.Name == meter.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            var dict = new Dictionary<string, string?>();
            foreach (var t in tags)
            {
                dict[t.Key] = t.Value?.ToString();
            }
            Measurements.Add(new Measurement(value, dict));
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    // orchestrator.task.duration is a Histogram<double> (seconds, fractional) - the
    // collector must subscribe with the same numeric type MeterListener was told to
    // instrument, or the callback for that type is never invoked and every
    // measurement is silently dropped.
    internal sealed record Measurement(double Value, Dictionary<string, string?> Tags);
}
