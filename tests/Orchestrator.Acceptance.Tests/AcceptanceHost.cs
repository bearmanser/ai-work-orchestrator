using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orchestrator.Adapters.AzureDevOps;
using Orchestrator.Application.Approvals;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.PullRequests;
using Orchestrator.Application.Repositories;
using Orchestrator.Application.Slack;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Operations;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;
using Orchestrator.Infrastructure.Jobs;
using Orchestrator.Infrastructure.Outbox;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Integration.Tests;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Orchestrator.Acceptance.Tests;

public sealed record QuestionHandle(Guid QuestionId, string ChannelId, string ThreadTs, string RecipientSlackUserId);
public sealed record FireOutcome(bool Succeeded, string? Error);
public sealed record ExecuteOutcome(bool Executed);

/// <summary>
/// The real system, end to end, for the recovery/duplicate-delivery/concurrency
/// acceptance suite (Task 49): a genuine ASP.NET Core host
/// (<see cref="AcceptanceApiFactory"/>, a <c>WebApplicationFactory&lt;Program&gt;</c>)
/// against a Testcontainers PostgreSQL database, a real local bare git
/// repository, WireMock standing in for Azure DevOps and Slack, and a
/// controllable <see cref="ManualTimeProvider"/>.
///
/// <see cref="RestartAsync"/> disposes the current <see cref="AcceptanceApiFactory"/>
/// (and with it its whole DI container - Hangfire storage handle, EF connection
/// pool, everything) and builds a brand new one bound to the *same* connection
/// string, git root and WireMock servers. That is deliberate: nothing here is
/// reset in memory, because "survives a restart" only means something if the
/// container actually goes away and comes back.
///
/// Two structural gaps this class had to work around are recorded in
/// <c>task-49-report.md</c> rather than silently patched over:
///  1. Nothing in production creates a step after the first ("planning") one -
///     no code creates "coding_agent.execute", "pull_request.create",
///     "pull_request.feedback" or "verification" steps. Every helper below that
///     needs one of those steps to exist creates it directly (exactly the way
///     <c>TaskIntakeService</c> creates the planning step), which is the
///     minimum test-side seeding needed to exercise those handlers at all.
///  2. No <c>IOperationReconciler</c> existed before this task, so
///     <c>OperationReconciliationService</c> could never actually resolve an
///     <c>Unknown</c> tool-call outcome. <c>PullRequestCreateReconciler</c>
///     (src/Orchestrator.Application/PullRequests) was added to make the
///     "crash after Azure DevOps created the PR" scenario provable at all.
/// </summary>
public sealed class AcceptanceHost(PostgresFixture postgres) : IAsyncLifetime
{
    // Unique per instance: every acceptance test class shares one Postgres
    // database (see AcceptanceCollection), and RepositoryProfile.RemoteUrl
    // has a unique index, so two classes cannot both seed "backend".
    private readonly string _repositoryName = "repo" + Guid.NewGuid().ToString("N")[..8];

    private string AzureDevOpsRemoteUrl => $"https://dev.azure.com/kameleon/Platform/_git/{_repositoryName}";

    // One Postgres container is shared by every acceptance test class (see
    // AcceptanceCollection) - starting three Testcontainers.PostgreSql
    // instances back to back in one process turned out to be more than this
    // sandbox's remote Docker daemon reliably handles (see
    // WaitForPostgresReadyAsync's remarks for the symptom this replaced).
    // Each class still gets its own git root, WireMock servers and API host -
    // only the database is shared, and every test resets its own task-scoped
    // rows first (see ResetAsync).
    private readonly PostgresFixture _postgres = postgres;
    private string _gitRoot = null!;
    private string _originPath = null!;
    private string _replaysDirectory = null!;
    private string _artifactsRoot = null!;
    private AcceptanceApiFactory _factory = null!;
    private HttpClient _client = null!;

    public FakeAzureDevOps AzureDevOps { get; private set; } = null!;
    public WireMockServer Slack { get; private set; } = null!;
    public ManualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
    public Guid RepositoryId { get; private set; }
    public Guid ConfirmerPersonId { get; private set; }
    public string ConfirmerSlackUserId { get; } = "U-confirmer";
    public string RequesterSlackUserId { get; } = "U-requester";
    public Guid RequesterPersonId { get; private set; }

    private string ConnectionString => _postgres.ConnectionString;

    public async Task InitializeAsync()
    {
        await WaitForPostgresReadyAsync();

        // Deliberately a very short prefix: Windows' default (non-long-path)
        // MAX_PATH is 260 characters, and a git ref log path already nests
        // several full 32-hex-character GUIDs (WorkspaceLayout's task and
        // repository ids) below this root - a verbose prefix here was enough,
        // in practice, to push a real ref path (e.g.
        // ".../.git/logs/refs/remotes/origin/orchestrator/{taskId}") over the
        // limit and fail a real push with a "path too long" error unrelated to
        // anything this suite is trying to prove.
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "oa-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        _gitRoot = Directory.CreateDirectory(Path.Combine(root, "git")).FullName;
        _replaysDirectory = Directory.CreateDirectory(Path.Combine(root, "agent-replays")).FullName;
        _artifactsRoot = Directory.CreateDirectory(Path.Combine(root, "artifacts")).FullName;
        _originPath = Path.Combine(root, "origin.git");

        CreateOriginRepository();

        AzureDevOps = new FakeAzureDevOps(_repositoryName);
        Slack = WireMockServer.Start();
        Slack.Given(Request.Create().WithPath("/chat.postMessage").UsingPost())
            .RespondWith(Response.Create().WithCallback(HandleSlackPostMessage));

        SetSecretEnvironmentVariables();

        // Seeded directly against the database (not through the API host, which
        // does not exist yet - it needs RepositoryId, below, to configure
        // TaskIntakeOptions) rather than via IRepositoryProfileStore/IPersonStore.
        RepositoryId = await SeedRepositoryProfileAsync();
        ConfirmerPersonId = await SeedPersonAsync(ConfirmerSlackUserId, "Confirmer", canConfirmPlans: true);
        RequesterPersonId = await SeedPersonAsync(RequesterSlackUserId, "Requester", canConfirmPlans: false);

        SeedReplayFixtures();

        await BuildFactoryAsync();
    }

    /// <summary>
    /// Testcontainers' own readiness wait considers the container ready once
    /// Postgres accepts connections *inside* the container; against this
    /// sandbox's remote Docker daemon (<c>DOCKER_HOST=tcp://127.0.0.1:2375</c>),
    /// the mapped host port can take a little longer to actually forward
    /// traffic - reliably enough to break a run that starts three of these
    /// containers back to back (one per acceptance test class) even though
    /// each one is completely reliable in isolation. A short real-connection
    /// retry loop here is the fix, not a longer sleep before it.
    /// </summary>
    private async Task WaitForPostgresReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        throw new InvalidOperationException("Postgres container never became reachable.", last);
    }

    public async Task DisposeAsync()
    {
        // Defensive: xUnit calls DisposeAsync even when InitializeAsync threw
        // partway through, so any of these may still be null.
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        AzureDevOps?.Stop();
        Slack?.Stop();
        Slack?.Dispose();
        // _postgres is owned by AcceptanceCollection (shared across every
        // acceptance test class), not by this host - it is not disposed here.
        if (_gitRoot is not null)
        {
            try { Directory.Delete(Path.GetDirectoryName(_gitRoot)!, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Truncates every task-scoped table (tasks, steps, outbox, plan revisions,
    /// tool-call records, ...) while leaving the reference data seeded once in
    /// <see cref="InitializeAsync"/> (the repository profile, the confirmer and
    /// requester people, their ownership grant) alone. Every test method in a
    /// class shares one <see cref="AcceptanceHost"/> (one Postgres container,
    /// one pair of WireMock servers, one git root - starting those per test
    /// would make an already-heavy suite far slower), so each test calls this
    /// first to start from a clean task history instead of tripping over
    /// steps or outbox messages a previous test in the same class left behind.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            DO $$ DECLARE
                r RECORD;
            BEGIN
                FOR r IN (
                    SELECT tablename FROM pg_tables
                    WHERE schemaname = 'orchestrator'
                      AND tablename NOT IN (
                          '__EFMigrationsHistory', 'people', 'ownership_entries', 'repositories',
                          'policy_configuration', 'integration_settings', 'admin_accounts', 'operational_flags')
                ) LOOP
                    EXECUTE 'TRUNCATE TABLE orchestrator.' || quote_ident(r.tablename) || ' CASCADE';
                END LOOP;
            END $$;
            """);
    }

    /// <summary>
    /// Disposes the current host and builds a brand new one against the same
    /// database, git root and WireMock servers. This is the whole mechanism
    /// "survives a restart" rests on - see the class remarks.
    /// </summary>
    public async Task RestartAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await BuildFactoryAsync();
    }

    private async Task BuildFactoryAsync()
    {
        _factory = new AcceptanceApiFactory
        {
            ConnectionString = ConnectionString,
            ArtifactsRoot = _artifactsRoot,
            GitRoot = _gitRoot,
            ReplaysDirectory = _replaysDirectory,
            AzureDevOpsBaseUrl = AzureDevOps.BaseUrl,
            SlackBaseUrl = Slack.Url!,
            Clock = Clock,
            RepositoryId = RepositoryId,
            FakeAzureDevOpsRemoteUrl = AzureDevOpsRemoteUrl,
            RepositoryName = _repositoryName,
            LocalOriginPath = _originPath,
        };
        _client = _factory.CreateClient();
        // Force the host to boot (and migrate/verify its DI graph) now rather
        // than lazily on the first request, so a bad restart fails the call that
        // caused it, not a later, unrelated assertion.
        await _client.GetAsync("/");
    }

    // ---------------------------------------------------------------- outbox

    /// <summary>
    /// No-op by construction: this host never dispatches the outbox
    /// automatically (Hangfire's own background worker is removed - see
    /// <see cref="AcceptanceApiFactory"/>), so every task submitted is already
    /// "paused" until <see cref="DrainOutboxAsync"/> is called explicitly.
    /// </summary>
    public Task PauseOutboxAsync() => Task.CompletedTask;

    /// <summary>
    /// Dispatches every currently-eligible outbox message. For a
    /// <c>step.dispatch</c> message this runs the real <c>StepExecutionJob</c>
    /// synchronously (see <see cref="AcceptanceApiFactory"/>'s override), so
    /// draining the outbox is what actually executes the next step.
    /// </summary>
    public async Task DrainOutboxAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        while (await dispatcher.DispatchBatchAsync(50, default) > 0)
        {
        }
    }

    /// <summary>
    /// Runs every currently-<c>Pending</c> step for real through
    /// <c>StepExecutionJob</c> (lease acquisition included), one at a time,
    /// until none remain. Used after <see cref="RunLeaseReclaimAsync"/> puts a
    /// reclaimed step back to Pending, and after
    /// <see cref="EnqueueDuplicateJobAsync"/> re-queues an already-handled one.
    /// </summary>
    public async Task DrainJobsAsync()
    {
        while (true)
        {
            Guid? stepId;
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                stepId = await db.Steps
                    .Where(s => s.Status == StepStatus.Pending)
                    .Select(s => (Guid?)s.Id)
                    .FirstOrDefaultAsync();
            }

            if (stepId is null)
                return;

            await using var runScope = _factory.Services.CreateAsyncScope();
            await runScope.ServiceProvider.GetRequiredService<StepExecutionJob>().RunAsync(stepId.Value, default);
        }
    }

    public async Task EnqueueDuplicateJobAsync(Guid taskId, string capability)
    {
        var stepId = await StepIdForAsync(taskId, capability);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<StepExecutionJob>().RunAsync(stepId, default);
    }

    // ------------------------------------------------------------- Slack in

    public string AppMentionPayload(string eventId, string? text = null, string? channelId = null, string? messageTs = null) =>
        JsonSerializer.Serialize(new
        {
            type = "event_callback",
            event_id = eventId,
            team_id = "T-1",
            @event = new
            {
                type = "app_mention",
                user = RequesterSlackUserId,
                channel = channelId ?? "C-acceptance",
                ts = messageTs ?? $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{Random.Shared.Next(1000, 9999)}",
                text = text ?? "add a GET endpoint",
            },
        });

    public async Task<HttpResponseMessage> PostSlackAsync(string payload)
    {
        const string signingSecret = "acceptance-slack-signing-secret";
        var timestamp = Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/slack")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        request.Headers.Add("X-Slack-Signature", ComputeSlackSignature(signingSecret, timestamp, payload));
        return await _client.SendAsync(request);
    }

    /// <summary>
    /// Posts an <c>app_mention</c> through the real webhook, drains the outbox
    /// so intake runs, and returns the created task id.
    /// </summary>
    public async Task<Guid> SubmitSlackTaskAsync(string text)
    {
        var channelId = "C-" + Guid.NewGuid().ToString("N")[..8];
        var messageTs = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{Random.Shared.Next(100000, 999999)}";
        var correlationId = $"slack:{channelId}:{messageTs}";

        var response = await PostSlackAsync(AppMentionPayload(
            "evt-" + Guid.NewGuid().ToString("N"), text, channelId, messageTs));
        response.EnsureSuccessStatusCode();
        await DrainOutboxAsync();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var task = await db.Tasks.SingleAsync(t => t.CorrelationId == correlationId);
        return task.Id;
    }

    public async Task<string> DumpStepsAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var rows = await db.Steps.Where(s => s.TaskId == taskId)
            .Select(s => new { s.Capability, s.Status, s.FailureReason })
            .ToListAsync();
        return string.Join("\n", rows.Select(r => $"{r.Capability}: {r.Status} {r.FailureReason}"));
    }

    public async Task<string> DumpOutboxAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var rows = await db.Outbox.OrderBy(m => m.OccurredAt)
            .Select(m => new { m.MessageType, m.ProcessedAt, m.AttemptCount, m.LastError, m.PayloadJson })
            .ToListAsync();
        return string.Join("\n", rows.Select(r =>
            $"{r.MessageType} processed={r.ProcessedAt} attempts={r.AttemptCount} error={r.LastError} payload={r.PayloadJson}"));
    }

    public async Task<int> CountTasksAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Tasks.CountAsync();
    }

    public async Task<int> CountOutboxMessagesAsync(string messageType)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Outbox.CountAsync(m => m.MessageType == messageType);
    }

    /// <summary>Creates a step for an already-seeded task and runs it once, for real.</summary>
    public async Task DispatchNewStepAsync(Guid taskId, string capability)
    {
        var stepId = await CreateStepAsync(taskId, capability);
        await RunStepAsync(stepId);
    }

    // --------------------------------------------------------- Azure DevOps in

    public string PrCommentPayload(string eventId, int threadId, int? pullRequestId = null) => JsonSerializer.Serialize(new
    {
        id = eventId,
        eventType = "ms.vss-code.git-pullrequest-comment-event",
        resource = new
        {
            comment = new { author = new { id = "U-reviewer" } },
            pullRequest = new
            {
                pullRequestId = pullRequestId ?? 1,
                repository = new { name = "backend" },
                lastMergeSourceCommit = new { commitId = "abc123" },
            },
        },
    });

    public async Task<HttpResponseMessage> PostAzureDevOpsAsync(string payload)
    {
        const string password = "acceptance-ado-webhook-password";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/azure-devops")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization",
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + password)));
        return await _client.SendAsync(request);
    }

    // ------------------------------------------------------------ task reads

    public async Task<WorkTask> LoadTaskAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
    }

    public async Task<string> CurrentPlanHashAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var revision = await db.PlanRevisions.AsNoTracking()
            .Where(r => r.TaskId == taskId)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstAsync();
        return revision.ContentHash;
    }

    /// <summary>
    /// The one <see cref="Approval"/> created for a task's plan confirmation
    /// (<c>PlanConfirmationService.PostForConfirmationAsync</c> calls
    /// <c>ApprovalService.RequestAsync</c> right after posting the revision).
    /// Used to prove an approval survives a restart both as data
    /// (<c>RequestedFrom</c>) and as an enforced rule (<see cref="ApproveAsync"/>
    /// still honours it afterwards).
    /// </summary>
    public async Task<Approval> ApprovalForTaskAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Approvals.AsNoTracking().SingleAsync(a => a.TaskId == taskId);
    }

    /// <summary>
    /// Records an approval decision through the real <see cref="ApprovalService"/>,
    /// exactly as <c>PlanConfirmationService.HandleInteractionAsync</c> would.
    /// </summary>
    public async Task<ApprovalResult> ApproveAsync(Guid approvalId, string approverId, string subjectHash)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var approvals = scope.ServiceProvider.GetRequiredService<ApprovalService>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        return await approvals.RecordDecisionAsync(
            approvalId, approverId, ApprovalDecision.Approved, subjectHash, clock.GetUtcNow(), default);
    }

    public async Task<int> AgentInvocationsForAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Database.SqlQuery<int>(
            $"SELECT COUNT(*)::int AS \"Value\" FROM orchestrator.acceptance_agent_invocations WHERE task_id = {taskId}")
            .SingleAsync();
    }

    public async Task<int> NoopInvocationsForAsync(Guid stepId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Database.SqlQuery<int>(
            $"SELECT COUNT(*)::int AS \"Value\" FROM orchestrator.acceptance_noop_invocations WHERE step_id = {stepId}")
            .SingleAsync();
    }

    public async Task<int> StepAttemptsAsync(Guid stepId) => await NoopInvocationsForAsync(stepId);

    public async Task<IReadOnlyList<PullRequestTracking>> TrackedPullRequestsAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Set<PullRequestTracking>().AsNoTracking().Where(t => t.TaskId == taskId).ToListAsync();
    }

    public async Task<IReadOnlyList<FeedbackItem>> FeedbackItemsAsync(Guid taskId) =>
        (await TrackedPullRequestsAsync(taskId)).SelectMany(t => t.Feedback).ToList();

    // ---------------------------------------------------------- plan/question flow

    /// <summary>
    /// Submits a task and drains the outbox so the real planning step runs
    /// once (through the real <c>ReplayAgentProvider</c>-backed pipeline).
    /// </summary>
    public async Task<Guid> RunToAgentInvocationAsync() => await SubmitSlackTaskAsync("add a GET endpoint");

    public async Task<(Guid TaskId, string Hash)> RunToPlanConfirmationAsync()
    {
        var taskId = await SubmitSlackTaskAsync("add a GET endpoint");
        var hash = await CurrentPlanHashAsync(taskId);
        return (taskId, hash);
    }

    public async Task<(Guid TaskId, QuestionHandle Question)> RunToBlockingQuestionAsync()
    {
        var taskId = await SubmitSlackTaskAsync("add a GET endpoint [BLOCKING_QUESTION]");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var question = await db.HumanQuestions.AsNoTracking().SingleAsync(q => q.TaskId == taskId);
        return (taskId, new QuestionHandle(question.Id, question.ChannelId, question.ThreadTs, RequesterSlackUserId));
    }

    public async Task AnswerQuestionAsync(QuestionHandle question, string text)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<HumanQuestionService>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await service.RecordAnswerAsync(
            question.ChannelId, question.ThreadTs, question.RecipientSlackUserId, text, clock.GetUtcNow(), default);
    }

    public async Task CancelTaskAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var task = await db.Tasks.SingleAsync(t => t.Id == taskId);
        task.Fire(TaskTrigger.Cancelled, clock.GetUtcNow());
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------- pipeline seeding

    /// <summary>
    /// Seeds a task with a confirmed plan, authorization envelope and budget
    /// ledger, walking the real <see cref="TaskStateMachine"/> through the
    /// triggers a real run would fire (PlanningStarted, PlanPosted,
    /// PlanConfirmed, WorkDispatched) up to <see cref="TaskState.Running"/>.
    /// No production code creates the next step after planning (see class
    /// remarks) - callers create the step for whichever capability they are
    /// about to exercise.
    /// </summary>
    private async Task<Guid> SeedConfirmedTaskAsync()
    {
        var now = Clock.GetUtcNow();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();

        var task = new WorkTask("add a GET endpoint", RequesterPersonId.ToString(), TaskSource.Slack,
            $"acceptance:{Guid.NewGuid()}", now);
        task.Fire(TaskTrigger.PlanningStarted, now);

        var envelope = new AuthorizationEnvelope(
            task.Id, RequesterPersonId.ToString(),
            [new RepositoryScope(RepositoryId, ["*"], ["/"], RepositoryAccess.Write)],
            [], [], ["replay"], [Domain.Authorization.AgentStage.Implementation, Domain.Authorization.AgentStage.Review],
            ["C-acceptance"], [RequesterSlackUserId], [], [],
            new BudgetLimits(1000m, TimeSpan.FromHours(24), 1000, 1000),
            now.AddDays(7), now);
        var ledger = new BudgetLedger(task.Id, envelope.Budget);

        var content = new PlanContent(
            "add a GET endpoint", ["backend"], [], "Add a read endpoint.",
            [new PlannedRepositoryChange(RepositoryId, "backend", ChangeIntent.Change, OriginTipSha())],
            ["dotnet test"], [], [], envelope.Budget, ["replay"], []);
        var revision = new PlanRevision(task.Id, 1, content);
        revision.Confirm(ConfirmerPersonId, now);

        task.Fire(TaskTrigger.PlanPosted, now);
        task.Fire(TaskTrigger.PlanConfirmed, now);
        task.Fire(TaskTrigger.WorkDispatched, now);

        db.Tasks.Add(task);
        db.Add(envelope);
        db.Add(ledger);
        db.Add(revision);
        await db.SaveChangesAsync();

        return task.Id;
    }

    private async Task<Guid> CreateStepAsync(Guid taskId, string capability)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var step = new Step(taskId, capability, null, null, clock.GetUtcNow());
        db.Steps.Add(step);
        await db.SaveChangesAsync();
        return step.Id;
    }

    private async Task<Guid> StepIdForAsync(Guid taskId, string capability)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Steps.Where(s => s.TaskId == taskId && s.Capability == capability)
            .OrderByDescending(s => s.CreatedAt).Select(s => s.Id).FirstAsync();
    }

    private async Task RunStepAsync(Guid stepId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<StepExecutionJob>().RunAsync(stepId, default);
    }

    public async Task<Guid> RunTaskToImplementationAsync(string _ = "backend")
    {
        var taskId = await SeedConfirmedTaskAsync();
        var stepId = await CreateStepAsync(taskId, "coding_agent.execute");
        await RunStepAsync(stepId);
        return taskId;
    }

    public async Task<string> WorkspacePathForAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var workspace = await db.Set<Workspace>().AsNoTracking().SingleAsync(w => w.TaskId == taskId);
        return workspace.Path;
    }

    public async Task<Guid> RunToPullRequestCreationAsync(bool crashAfterRemoteSuccess = false)
    {
        var taskId = await RunTaskToImplementationAsync();
        var stepId = await CreateStepAsync(taskId, "pull_request.create");

        if (crashAfterRemoteSuccess)
        {
            // Simulate the remote side effect having actually happened (a real
            // create call against the fake Azure DevOps, so AzureDevOps.CreateCalls
            // and FindOpenBySourceBranch both see it) while the *local* record of
            // that fact is left exactly where a crash between "gateway recorded
            // intent" and "gateway recorded success" would leave it: Unknown.
            var workBranch = GitBranchNaming.WorkBranchFor(taskId);
            await SimulateCrashedPullRequestCreateAsync(taskId, workBranch);
        }
        else
        {
            await RunStepAsync(stepId);
        }

        return taskId;
    }

    public async Task<Guid> RunTaskToPullRequestAsync()
    {
        var taskId = await RunToPullRequestCreationAsync();
        return taskId;
    }

    private async Task SimulateCrashedPullRequestCreateAsync(Guid taskId, string workBranch)
    {
        // Hits the fake Azure DevOps API's real create endpoint directly (the
        // same one PullRequestCreationStepHandler would call through the
        // gateway) so the "remote side effect" is genuinely real and tracked by
        // FakeAzureDevOps - only the *local* record of it is left crashed.
        var sourceRef = "refs/heads/" + workBranch;
        using var httpClient = new HttpClient { BaseAddress = new Uri(AzureDevOps.BaseUrl) };
        var body = JsonSerializer.Serialize(new
        {
            sourceRefName = sourceRef,
            targetRefName = "refs/heads/main",
            title = "Test",
            description = "Test",
            reviewers = Array.Empty<object>(),
        });
        await httpClient.PostAsync(
            $"/kameleon/Platform/_apis/git/repositories/{_repositoryName}/pullrequests?api-version=7.1",
            new StringContent(body, Encoding.UTF8, "application/json"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        db.ToolCallRecords.Add(new ToolCallRecord(
            Guid.CreateVersion7(), taskId, Guid.CreateVersion7(), ActionKind.PullRequestCreate,
            Guid.CreateVersion7().ToString("N"), $"pr:{taskId}:{RepositoryId}",
            ToolCallOutcome.Unknown, new PolicyDecision.Permitted("test"), null, clock.GetUtcNow(), null));
        await db.SaveChangesAsync();
    }

    public async Task RetryStepAsync(Guid taskId, string capability)
    {
        // Proves the gateway's own retry gate (IToolCallStore's Unknown-outcome
        // guard) directly, which is the actual mechanism this scenario is about -
        // see the class remarks on why PullRequestCreationStepHandler's own
        // "adopt an already-open PR" fast path would otherwise short-circuit
        // this before the gateway is ever consulted.
        if (capability != "pull_request.create")
            throw new NotSupportedException($"RetryStepAsync only supports 'pull_request.create', got '{capability}'.");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var gateway = scope.ServiceProvider.GetRequiredService<IToolGateway>();
        var host = scope.ServiceProvider.GetRequiredService<IPullRequestHost>();

        var envelope = await db.Set<AuthorizationEnvelope>().AsNoTracking().SingleAsync(e => e.TaskId == taskId);
        var ledger = await db.Budgets.SingleAsync(l => l.TaskId == taskId);
        var workBranch = GitBranchNaming.WorkBranchFor(taskId);

        var call = new ToolCall(
            taskId, Guid.CreateVersion7(), "orchestrator", ActionKind.PullRequestCreate,
            ResourceScope.Repository(RepositoryId, workBranch),
            Guid.CreateVersion7().ToString("N"), $"pr:{taskId}:{RepositoryId}",
            $"Create pull request for '{_repositoryName}'.", envelope, ledger);

        await gateway.InvokeAsync(call, ct => host.CreateAsync(new CreatePullRequest(
            "kameleon", "Platform", _repositoryName, workBranch, "main", "Test", "Test", []), ct), default);
    }

    public async Task<ReconciliationSummary> ReconcileAsync(Guid taskId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OperationReconciliationService>()
            .ReconcileAsync(taskId, default);
    }

    // ------------------------------------------------------------------ leases

    public async Task<(Guid TaskId, Guid StepId)> LeaseStepAndAbandonAsync()
    {
        var taskId = await SeedConfirmedTaskAsync();
        var stepId = await CreateStepAsync(taskId, "acceptance.noop");

        await using var scope = _factory.Services.CreateAsyncScope();
        var leases = scope.ServiceProvider.GetRequiredService<ILeaseManager>();
        await leases.TryAcquireAsync(stepId, "abandoned-worker", TimeSpan.FromMinutes(5), default);
        return (taskId, stepId);
    }

    public async Task RunLeaseReclaimAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILeaseManager>().ReclaimExpiredAsync(default);
    }

    public async Task<Guid> SeedPendingStepAsync()
    {
        var taskId = await SeedConfirmedTaskAsync();
        return await CreateStepAsync(taskId, "acceptance.noop");
    }

    public async Task<ExecuteOutcome> TryExecuteStepAsync(Guid stepId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var leases = scope.ServiceProvider.GetRequiredService<ILeaseManager>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<StepDispatcher>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var step = await db.Steps.SingleAsync(s => s.Id == stepId);
        if (step.Status is not (StepStatus.Pending or StepStatus.Leased))
            return new ExecuteOutcome(false);

        var owner = $"worker-{Guid.NewGuid():N}";
        if (!await leases.TryAcquireAsync(stepId, owner, TimeSpan.FromMinutes(5), default))
            return new ExecuteOutcome(false);

        await db.Entry(step).ReloadAsync();
        var outcome = await dispatcher.DispatchAsync(
            new StepContext(step.TaskId, step.Id, step.Capability, step.InputArtifactId), default);

        var now = clock.GetUtcNow();
        if (outcome.FailureReason is not null)
            step.Fail(outcome.FailureReason, now);
        else
            step.Succeed(outcome.OutputArtifactId, now);
        await db.SaveChangesAsync();
        return new ExecuteOutcome(true);
    }

    // -------------------------------------------------------------- task transitions

    public async Task<Guid> SeedTaskAsync(TaskState state)
    {
        if (state != TaskState.WaitingForPlanConfirmation)
            throw new NotSupportedException($"SeedTaskAsync only supports {nameof(TaskState.WaitingForPlanConfirmation)}.");

        var now = Clock.GetUtcNow();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var task = new WorkTask("add a GET endpoint", RequesterPersonId.ToString(), TaskSource.Slack,
            $"acceptance:{Guid.NewGuid()}", now);
        task.Fire(TaskTrigger.PlanningStarted, now);
        task.Fire(TaskTrigger.PlanPosted, now);
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    /// <summary>
    /// Fires one trigger against the task using a fresh scope (and therefore a
    /// fresh <see cref="OrchestratorDbContext"/>) per call, so two concurrent
    /// calls genuinely race the same Postgres row through EF's xmin-based
    /// optimistic concurrency check rather than sharing in-process state.
    /// </summary>
    public async Task<FireOutcome> FireAsync(Guid taskId, TaskTrigger trigger)
    {
        try
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            var task = await db.Tasks.SingleAsync(t => t.Id == taskId);
            task.Fire(trigger, clock.GetUtcNow());
            await db.SaveChangesAsync();
            return new FireOutcome(true, null);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return new FireOutcome(false, ex.Message);
        }
        catch (InvalidTransitionException ex)
        {
            return new FireOutcome(false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- git / seeding

    /// <summary>
    /// Seeded directly against Postgres via <see cref="PostgresFixture.NewDbContext"/>,
    /// not through the API host: at this point in <see cref="InitializeAsync"/>
    /// the host does not exist yet, because building it needs
    /// <see cref="RepositoryId"/> (to configure <c>TaskIntakeOptions</c>).
    /// </summary>
    private async Task<Guid> SeedRepositoryProfileAsync()
    {
        var profile = new RepositoryProfile(
            "backend", AzureDevOpsRemoteUrl, "main", "Acceptance test repository",
            Guid.CreateVersion7().ToString(),
            RepositoryAccess.Write,
            new BuildCommands(null, null, "dotnet test", null, ".", TimeSpan.FromMinutes(5)),
            []);
        await using var db = _postgres.NewDbContext();
        db.Repositories.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private async Task<Guid> SeedPersonAsync(string slackUserId, string displayName, bool canConfirmPlans)
    {
        var person = new PersonIdentity(Guid.CreateVersion7(), displayName, slackUserId, null, null, isActive: true);
        await using var db = _postgres.NewDbContext();
        db.People.Add(person);

        if (canConfirmPlans)
        {
            db.OwnershipEntries.Add(new OwnershipEntry(
                Guid.CreateVersion7(), OwnershipSubject.Repository, RepositoryId.ToString(),
                person.Id, CanConfirmPlans: true, CanApproveRestrictedActions: true));
        }

        await db.SaveChangesAsync();
        return person.Id;
    }

    /// <summary>
    /// Creates a fresh developer working copy of the fixture's origin
    /// repository (a plain clone, entirely separate from anything under the
    /// orchestrator's own git root) with one uncommitted change, and returns
    /// the changed file's path.
    /// </summary>
    public async Task<string> CreateDeveloperWorkingCopyChangeAsync(string content)
    {
        var developerPath = Path.Combine(Path.GetTempPath(), "orchestrator-acceptance-dev-" + Guid.NewGuid().ToString("N"));
        await Task.Run(() => Repository.Clone(_originPath, developerPath));
        var filePath = Path.Combine(developerPath, "developer-work-in-progress.txt");
        await File.WriteAllTextAsync(filePath, content);
        return filePath;
    }

    public async Task RunTaskToPullRequestAsyncNoReturn() => await RunTaskToPullRequestAsync();

    private void CreateOriginRepository()
    {
        Repository.Init(_originPath, isBare: true);
        var seedPath = _originPath + "-seed";
        Repository.Clone(_originPath, seedPath);
        var signature = new Signature("acceptance", "acceptance@example.com", Clock.GetUtcNow());
        using (var seed = new Repository(seedPath))
        {
            File.WriteAllText(Path.Combine(seedPath, "README.md"), "seed\n");
            Commands.Stage(seed, "*");
            seed.Commit("initial commit", signature, signature);
            if (seed.Head.FriendlyName != "main")
                seed.Branches.Rename(seed.Head.FriendlyName, "main");
            Commands.Checkout(seed, seed.Branches["main"]);
            var remote = seed.Network.Remotes["origin"];
            seed.Network.Push(remote, "refs/heads/main");
        }

        foreach (var file in Directory.EnumerateFiles(seedPath, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(seedPath, recursive: true);
    }

    private string OriginTipSha()
    {
        using var repo = new Repository(_originPath);
        return repo.Branches["main"].Tip.Sha;
    }

    private static void SetSecretEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_SLACK_SIGNING_SECRET", "acceptance-slack-signing-secret");
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_SLACK_BOT_TOKEN", "acceptance-slack-bot-token");
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_AZUREDEVOPS_WEBHOOK_PASSWORD", "acceptance-ado-webhook-password");
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_AZUREDEVOPS_PAT", "acceptance-ado-pat");
        Environment.SetEnvironmentVariable("ORCHESTRATOR_SECRET_GIT_BACKEND_CREDENTIAL", "unused-local-remote");
    }

    private void SeedReplayFixtures()
    {
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent-replays");
        var repositoryIdPlaceholder = "__REPOSITORY_ID__";
        var baselinePlaceholder = "__BASELINE_COMMIT__";
        var baseline = OriginTipSha();

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.json"))
        {
            var text = File.ReadAllText(file)
                .Replace(repositoryIdPlaceholder, RepositoryId == Guid.Empty ? Guid.Empty.ToString() : RepositoryId.ToString())
                .Replace(baselinePlaceholder, baseline);
            File.WriteAllText(Path.Combine(_replaysDirectory, Path.GetFileName(file)), text);
        }
    }

    private static WireMock.ResponseMessage HandleSlackPostMessage(WireMock.IRequestMessage request) => new()
    {
        StatusCode = 200,
        BodyData = new WireMock.Util.BodyData
        {
            BodyAsJson = new { ok = true, ts = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{Random.Shared.Next(1000, 9999)}" },
            DetectedBodyType = WireMock.Types.BodyType.Json,
        },
    };

    private static string ComputeSlackSignature(string secret, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"));
        return "v0=" + Convert.ToHexStringLower(hash);
    }
}
