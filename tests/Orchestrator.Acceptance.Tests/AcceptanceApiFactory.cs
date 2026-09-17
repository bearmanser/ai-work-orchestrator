using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orchestrator.Adapters.AzureDevOps;
using Orchestrator.Adapters.Git;
using Orchestrator.Adapters.Slack;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Slack;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Budgets;
using Orchestrator.Infrastructure.Jobs;
using Orchestrator.Infrastructure.Outbox;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Integration.Tests;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// One instantiation of the real API host (built on the same
/// <c>WebApplicationFactory&lt;Program&gt;</c> pattern as
/// <c>OrchestratorApiFactory</c>), wired for the acceptance suite: the same
/// Postgres database and git root as its predecessor/successor across a
/// restart, WireMock standing in for Azure DevOps and Slack, and the real
/// <c>ICodingAgent</c> pipeline swapped for <see cref="AcceptanceReplayAgent"/>
/// so the suite never calls a real agent CLI. Unlike production, Hangfire's own
/// background worker is not started (see the constructor's remarks) - the
/// acceptance host drives every job explicitly through
/// <see cref="AcceptanceHost"/>'s Drain*/Run* methods so restart, duplicate and
/// concurrency tests observe deterministic, one-step-at-a-time execution
/// instead of racing a live background thread pool.
/// </summary>
public sealed class AcceptanceApiFactory : WebApplicationFactory<Program>
{
    public required string ConnectionString { get; init; }
    public required string ArtifactsRoot { get; init; }
    public required string GitRoot { get; init; }
    public required string ReplaysDirectory { get; init; }
    public required string AzureDevOpsBaseUrl { get; init; }
    public required string SlackBaseUrl { get; init; }
    public required ManualTimeProvider Clock { get; init; }
    public required Guid RepositoryId { get; init; }
    public required string FakeAzureDevOpsRemoteUrl { get; init; }
    public required string LocalOriginPath { get; init; }
    public required string RepositoryName { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Orchestrator", ConnectionString);
        builder.UseSetting("Orchestrator:Artifacts:Root", ArtifactsRoot);
        builder.UseSetting("Orchestrator:Git:Root", GitRoot);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // Production wires exactly one ReplayAgentProvider (keyed by a random
            // OperationId nobody could pre-record a fixture for) plus the real
            // Claude/Codex CLI providers, none of which the acceptance suite may
            // call (see task-49-report.md). AcceptanceReplayAgent replaces all three.
            services.RemoveAll<ICodingAgent>();
            services.AddScoped<ICodingAgent>(_ => new AcceptanceReplayAgent(ReplaysDirectory, ConnectionString));

            services.RemoveAll<TaskIntakeOptions>();
            services.AddSingleton(new TaskIntakeOptions
            {
                DefaultRepositoryIds = [RepositoryId],
                DefaultAllowedProviders = ["replay"],
                DefaultBudget = new BudgetLimits(1000m, TimeSpan.FromHours(24), 1000, 1000),
            });

            // See RemoteRewritingGitWorkspace's remarks for why this decorator
            // exists instead of a git-level url.insteadOf redirect.
            services.RemoveAll<IGitWorkspace>();
            services.AddScoped<IGitWorkspace>(sp => new RemoteRewritingGitWorkspace(
                new LibGit2Workspace(
                    GitRoot,
                    sp.GetRequiredService<ISecretStore>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IWorkspaceStore>()),
                FakeAzureDevOpsRemoteUrl,
                LocalOriginPath));

            services.RemoveAll<AzureDevOpsOptions>();
            services.AddSingleton(new AzureDevOpsOptions
            {
                BaseUrl = AzureDevOpsBaseUrl,
                Organization = "kameleon",
                Project = "Platform",
                RepositoryName = RepositoryName,
                PatSecret = new SecretRef("azuredevops.pat"),
            });

            // SlackChatGateway sends relative-URI requests against the typed
            // client's own BaseAddress (unlike AzureDevOpsHttpClient, which
            // builds an absolute URI from AzureDevOpsOptions itself) - the
            // client registration must be overridden, not just the options.
            services.AddHttpClient<IChatGateway, SlackChatGateway>(
                client => client.BaseAddress = new Uri(SlackBaseUrl));

            // The acceptance suite drives step execution itself (see
            // AcceptanceHost.DrainOutboxAsync/DrainJobsAsync) instead of letting
            // Hangfire's own background server race it asynchronously - the whole
            // point of a restart/duplicate/concurrency suite is to control exactly
            // when each step is attempted. AddHangfire (Postgres storage, and the
            // RecurringJob.AddOrUpdate calls in Program.cs's top-level statements)
            // stays in place; only the live worker that would otherwise process the
            // queue in the background is removed.
            var hangfireHostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType?.Namespace?.StartsWith("Hangfire", StringComparison.Ordinal) == true)
                .ToList();
            foreach (var descriptor in hangfireHostedServices)
                services.Remove(descriptor);

            // Mirrors Program.cs's own OutboxDispatcher wiring exactly, except the
            // "step.dispatch" branch runs the real StepExecutionJob synchronously
            // instead of enqueuing it to the (now inert) Hangfire queue - draining
            // the outbox is what makes the step run, deterministically, in the
            // acceptance host.
            services.RemoveAll<OutboxDispatcher>();
            services.AddScoped<OutboxDispatcher>(sp => new OutboxDispatcher(
                () => new OrchestratorDbContext(
                    new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(ConnectionString).Options),
                async (message, ct) =>
                {
                    using var document = JsonDocument.Parse(message.PayloadJson);
                    if (document.RootElement.TryGetProperty("stepId", out var stepIdElement)
                        && Guid.TryParse(stepIdElement.GetString(), out var stepId))
                    {
                        using var jobScope = sp.CreateScope();
                        await jobScope.ServiceProvider.GetRequiredService<StepExecutionJob>().RunAsync(stepId, ct);
                    }
                    else if (message.MessageType == "slack.event")
                    {
                        var inbound = JsonSerializer.Deserialize<SlackInbound>(
                            message.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (inbound is not null)
                        {
                            using var jobScope = sp.CreateScope();
                            await jobScope.ServiceProvider.GetRequiredService<SlackEventStepHandler>()
                                .HandleSlackEventAsync(inbound, ct);
                        }
                    }
                },
                sp.GetRequiredService<TimeProvider>()));

            // Additive: a test-only capability the concurrency suite dispatches
            // directly, so the lease-racing proof does not need a full
            // envelope/budget/workspace setup irrelevant to what it is proving.
            services.AddScoped<IStepHandler>(_ => new NoopStepHandler(ConnectionString));
        });
    }
}
