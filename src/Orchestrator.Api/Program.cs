using System.Diagnostics.Metrics;
using System.Text.Json;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using Orchestrator.Adapters.Agent;
using Orchestrator.Adapters.Agent.Providers;
using Orchestrator.Adapters.Git;
using Orchestrator.Adapters.Obsidian;
using Orchestrator.Adapters.P1;
using Orchestrator.Adapters.Slack;
using Orchestrator.Api.Auth;
using Orchestrator.Api.Dashboard;
using Orchestrator.Api.Endpoints;
using Orchestrator.Api.Webhooks;
using Orchestrator.Application.Agents;
using Orchestrator.Application.Approvals;
using Orchestrator.Application.Checks;
using Orchestrator.Application.Context;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Meetings;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Application.Slack;
using Orchestrator.Application.Steps;
using Orchestrator.Infrastructure.Artifacts;
using Orchestrator.Infrastructure.Audit;
using Orchestrator.Infrastructure.Identity;
using Orchestrator.Infrastructure.Inbox;
using Orchestrator.Infrastructure.Jobs;
using Orchestrator.Infrastructure.Operations;
using Orchestrator.Infrastructure.Outbox;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Secrets;
using Orchestrator.Infrastructure.Telemetry;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Orchestrator")
    ?? Environment.GetEnvironmentVariable("ORCHESTRATOR_CONNECTION_STRING")
    ?? throw new InvalidOperationException(
        "Orchestrator database connection string is not configured. Set ConnectionStrings:Orchestrator or ORCHESTRATOR_CONNECTION_STRING.");

builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer(options => options.WorkerCount = 4);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ILeaseManager, LeaseManager>();
builder.Services.AddScoped<IJobScheduler, HangfireJobScheduler>();
builder.Services.AddScoped<StepDispatcher>();
builder.Services.AddScoped<StepExecutionJob>();
builder.Services.AddScoped<OutboxPollingJob>();
builder.Services.AddScoped<LeaseReclaimJob>();
builder.Services.AddScoped<OutboxDispatcher>(sp => new OutboxDispatcher(
    () => new OrchestratorDbContext(
        new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options),
    async (message, ct) =>
    {
        using var document = JsonDocument.Parse(message.PayloadJson);
        if (document.RootElement.TryGetProperty("stepId", out var stepIdElement)
            && Guid.TryParse(stepIdElement.GetString(), out var stepId))
        {
            await sp.GetRequiredService<IJobScheduler>().EnqueueStepAsync(stepId, ct);
        }
        else if (message.MessageType == "slack.event")
        {
            var inbound = JsonSerializer.Deserialize<SlackInbound>(
                message.PayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (inbound is not null)
            {
                await sp.GetRequiredService<SlackEventStepHandler>()
                    .HandleSlackEventAsync(inbound, ct);
            }
        }
    },
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<IAuditWriter, AuditWriter>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton(PolicyRules.Default);
builder.Services.AddScoped<PolicyEngine>(sp => new PolicyEngine(
    sp.GetRequiredService<IAuditWriter>(),
    PolicyRules.Default,
    sp.GetRequiredService<IOperationalControls>()));
// EF-backed so an Unknown outcome - and the retry gate it enforces - survives a
// process restart; InMemoryToolCallStore remains for tests that want no database.
builder.Services.AddScoped<IToolCallStore, EfToolCallStore>();
builder.Services.AddScoped<OperationReconciliationService>();
builder.Services.AddScoped<IToolGateway, ToolGateway>();
builder.Services.AddScoped<IRepositoryProfileStore, EfRepositoryProfileStore>();
builder.Services.AddScoped<RepositoryCatalogService>();
builder.Services.AddScoped<IRelationshipStore, EfRelationshipStore>();
builder.Services.AddScoped<ISecretStore, EnvironmentSecretStore>();
builder.Services.AddScoped<IEventRegistry, EventRegistry>();
builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddSingleton<IPersonStore, InMemoryPersonStore>();
builder.Services.AddSingleton<IOwnershipStore, InMemoryOwnershipStore>();
builder.Services.AddScoped<IdentityResolver>();
builder.Services.AddScoped<OwnershipDirectory>();
builder.Services.AddScoped<ITaskIntakeStore, EfTaskIntakeStore>();
builder.Services.AddScoped<IOperationalControls, EfOperationalControls>();
builder.Services.AddScoped<OperationalControlService>();
builder.Services.AddScoped<TaskIntakeService>();
builder.Services.AddSingleton<ITranscriptClassifier, TranscriptClassifier>();
builder.Services.AddScoped<MeetingAssignmentService>();
builder.Services.AddSingleton<IMeetingSegmentStore, InMemoryMeetingSegmentStore>();
builder.Services.AddSingleton<IMeetingTaskStore, InMemoryMeetingTaskStore>();
builder.Services.AddScoped<SlackEventStepHandler>();
builder.Services.AddHttpClient<IChatGateway, SlackChatGateway>(client =>
    client.BaseAddress = new Uri("https://slack.com/api/"));
builder.Services.AddScoped<IHumanQuestionStore, EfHumanQuestionStore>();
builder.Services.AddScoped<IQuestionTaskStore, EfHumanQuestionStore>();
builder.Services.AddScoped<IQuestionStepLeases, EfHumanQuestionStore>();
builder.Services.AddSingleton(new WorkingDayCalculator(new HashSet<DateOnly>()));
builder.Services.AddScoped<HumanQuestionService>();
builder.Services.AddScoped<QuestionEscalationJob>();

builder.Services.AddScoped<IArtifactStore>(sp =>
    new FileSystemArtifactStore(
        builder.Configuration["Orchestrator:Artifacts:Root"] ?? "artifacts",
        sp.GetRequiredService<OrchestratorDbContext>(),
        sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<ITaskQueryStore, EfTaskQueryStore>();

var taskIntakeOptions = new TaskIntakeOptions();
builder.Configuration.GetSection("Orchestrator:Intake").Bind(taskIntakeOptions);
builder.Services.AddSingleton(taskIntakeOptions);

var gitRoot = builder.Configuration["Orchestrator:Git:Root"] ?? string.Empty;
builder.Services.AddSingleton(new GitOptions { Root = gitRoot });
builder.Services.AddScoped<IWorkspaceFileReader, MirrorFileReader>();
builder.Services.AddScoped<IWorkspaceStore, EfWorkspaceStore>();
builder.Services.AddScoped<IGitWorkspace>(sp => new LibGit2Workspace(
    gitRoot,
    sp.GetRequiredService<ISecretStore>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IWorkspaceStore>()));

builder.Services.AddScoped<SandboxedProcessRunner>();
var claudeOptions = new ClaudeCliOptions();
builder.Configuration.GetSection("AgentProviders:claude").Bind(claudeOptions);
builder.Services.AddSingleton(claudeOptions);
var codexOptions = builder.Configuration.GetSection("AgentProviders:codex").Get<CodexCliOptions>()
    ?? new CodexCliOptions(
        "orchestrator/worker:latest",
        ["codex"],
        ["exec", "--json", "--sandbox", "workspace-write", "--cd", "/work"],
        "Stdin",
        "openai.api-key",
        "OPENAI_API_KEY",
        "gpt-5-codex",
        ["api.openai.com"]);
builder.Services.AddSingleton(codexOptions);
builder.Services.AddScoped<ICodingAgent, ReplayAgentProvider>(sp => new ReplayAgentProvider(
    builder.Configuration["Orchestrator:Agents:ReplaysDirectory"] ?? "agent-replays"));
builder.Services.AddScoped<ICodingAgent, ClaudeCliProvider>();
builder.Services.AddScoped<ICodingAgent, CodexCliProvider>();
builder.Services.AddScoped<AgentRouter>();

builder.Services.AddScoped<IPlanningScopeStore, EfPlanningScopeStore>();
builder.Services.AddScoped<IContextSnapshotStore, EfContextSnapshotStore>();
builder.Services.AddScoped<IAgentRunStore, EfAgentRunStore>();
builder.Services.AddScoped<IDomainDataSource>(sp => new NamedQueryDomainDataSource(
    new P1Options(Path.Combine(builder.Environment.ContentRootPath, "config", "p1-named-queries.json")),
    sp.GetRequiredService<ISecretStore>()));
builder.Services.AddScoped<CheckRunner>();
builder.Services.AddScoped<ISandboxExecutor, SandboxExecutorAdapter>();

var checkRunnerOptions = new CheckRunnerOptions();
builder.Configuration.GetSection("Orchestrator:Checks").Bind(checkRunnerOptions);
builder.Services.AddSingleton(checkRunnerOptions);
builder.Services.AddScoped<ImplementationStepHandler>();
builder.Services.AddScoped<IStepHandler, ImplementationStepHandler>();
builder.Services.AddScoped<VerificationStepHandler>();
builder.Services.AddScoped<IStepHandler, VerificationStepHandler>();
builder.Services.AddSingleton<IApprovalStore, InMemoryApprovalStore>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<IPlanConfirmationStore, EfPlanConfirmationStore>();
builder.Services.AddScoped<PlanConfirmationService>();
builder.Services.AddScoped<ContextAssembler>();
builder.Services.AddScoped<PlanningStepHandler>();
builder.Services.AddScoped<IStepHandler, PlanningStepHandler>();

var planningOptions = new PlanningOptions();
builder.Configuration.GetSection("Orchestrator:Planning").Bind(planningOptions);
builder.Services.AddSingleton(planningOptions);

var planConfirmationOptions = new PlanConfirmationOptions();
builder.Configuration.GetSection("Orchestrator:Plans").Bind(planConfirmationOptions);
builder.Services.AddSingleton(planConfirmationOptions);

var vaultRoot = builder.Configuration["Orchestrator:Vault:Root"] ?? string.Empty;
if (string.IsNullOrWhiteSpace(vaultRoot))
{
    builder.Services.AddSingleton<IVaultStore, NullVaultStore>();
}
else
{
    var vaultIndexDirectory = builder.Configuration["Orchestrator:Vault:IndexDirectory"]
        ?? Path.Combine(vaultRoot, ".orchestrator-index");
    builder.Services.AddSingleton<IVaultStore>(sp => new FileSystemVaultStore(
        vaultRoot, vaultIndexDirectory, sp.GetRequiredService<TimeProvider>()));
}

var meetingTranscriptOptions = new MeetingTranscriptOptions();
builder.Configuration.GetSection("Orchestrator:Meetings").Bind(meetingTranscriptOptions);
builder.Services.AddSingleton(meetingTranscriptOptions);

var gitCatalogOptions = new GitCatalogOptions();
builder.Configuration.GetSection("Orchestrator:Git").Bind(gitCatalogOptions);
builder.Services.AddSingleton(gitCatalogOptions);

// SandboxOptions.DockerCommand defaults to ["docker"] - what production (Linux) has on PATH.
// Bind() is deliberately not used here: DockerCommand's default is itself a non-empty list, and
// the configuration binder appends configured array entries onto an existing IReadOnlyList<T>
// instance rather than replacing it, which would silently merge the two instead of overriding.
var sandboxOptions = new SandboxOptions();
var configuredDockerCommand = builder.Configuration.GetSection("Sandbox:DockerCommand").Get<string[]>();
if (configuredDockerCommand is { Length: > 0 })
    sandboxOptions.DockerCommand = configuredDockerCommand;
builder.Services.AddSingleton(sandboxOptions);

// OpenTelemetry metrics
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(OrchestratorMetrics.MeterName)
        .AddPrometheusExporter());

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "orchestrator_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AdminOnly.PolicyName, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim("admin", "true"));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfMiddleware>();
app.MapPrometheusScrapingEndpoint();

app.MapGet("/", () => "Hello World!");
AuthEndpoints.Map(app);
RepositoryEndpoints.Map(app);
DirectoryEndpoints.Map(app);
RelationshipEndpoints.Map(app);
TranscriptEndpoints.Map(app);
OperationsEndpoints.Map(app);
TaskOperationsEndpoints.Map(app);
AzureDevOpsWebhookEndpoint.Map(app);
SlackWebhookEndpoint.Map(app);
SlackInteractiveEndpoint.Map(app);
TaskQueryEndpoints.Map(app);
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new AdminOnlyDashboardFilter()],
});

RecurringJob.AddOrUpdate<OutboxPollingJob>(
    "outbox-dispatch", job => job.RunAsync(CancellationToken.None), "*/5 * * * * *");
RecurringJob.AddOrUpdate<LeaseReclaimJob>(
    "lease-reclaim", job => job.RunAsync(CancellationToken.None), Cron.Minutely);
RecurringJob.AddOrUpdate<QuestionEscalationJob>(
    "question-escalation", job => job.RunAsync(CancellationToken.None), Cron.Hourly);

app.Run();

public partial class Program;
