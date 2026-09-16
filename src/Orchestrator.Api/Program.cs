using System.Text.Json;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Api.Auth;
using Orchestrator.Api.Dashboard;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
using Orchestrator.Infrastructure.Audit;
using Orchestrator.Infrastructure.Identity;
using Orchestrator.Infrastructure.Jobs;
using Orchestrator.Infrastructure.Outbox;
using Orchestrator.Infrastructure.Persistence;

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
    },
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<IAuditWriter, AuditWriter>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

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

app.MapGet("/", () => "Hello World!");
AuthEndpoints.Map(app);
// Placeholder so /api/admin/* carries the AdminOnly policy before the real
// admin surface (Tasks 44-48) exists. Authorized callers get 404; everyone
// else gets 401/403 without learning anything about the surface.
app.Map("/api/admin/{**catchAll}", () => Results.NotFound())
    .RequireAuthorization(AdminOnly.PolicyName);
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new AdminOnlyDashboardFilter()],
});

RecurringJob.AddOrUpdate<OutboxPollingJob>(
    "outbox-dispatch", job => job.RunAsync(CancellationToken.None), "*/5 * * * * *");
RecurringJob.AddOrUpdate<LeaseReclaimJob>(
    "lease-reclaim", job => job.RunAsync(CancellationToken.None), Cron.Minutely);

app.Run();

public partial class Program;
