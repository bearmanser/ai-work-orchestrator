using System.Text.Json;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Api.Dashboard;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
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

var app = builder.Build();

app.MapGet("/", () => "Hello World!");
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new AdminOnlyDashboardFilter()],
});

RecurringJob.AddOrUpdate<OutboxPollingJob>(
    "outbox-dispatch", job => job.RunAsync(CancellationToken.None), "*/5 * * * * *");
RecurringJob.AddOrUpdate<LeaseReclaimJob>(
    "lease-reclaim", job => job.RunAsync(CancellationToken.None), Cron.Minutely);

app.Run();
