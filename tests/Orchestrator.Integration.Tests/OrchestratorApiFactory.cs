using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orchestrator.Integration.Tests;

/// <summary>
/// Boots the real API host against the shared Postgres fixture database.
/// The connection string override must be set from the test class constructor
/// before the first client is created, because the host builds lazily.
/// </summary>
public sealed class OrchestratorApiFactory : WebApplicationFactory<Program>
{
    public static string? ConnectionString { get; set; }
    public static string ArtifactsRoot { get; set; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Orchestrator.Api", "artifacts"));

    internal ManualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-09-16T10:00:00Z"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(
            "ConnectionStrings:Orchestrator",
            ConnectionString ?? throw new InvalidOperationException(
                "Set OrchestratorApiFactory.ConnectionString from the test fixture before creating a client."));

        builder.UseSetting("Orchestrator:Artifacts:Root", ArtifactsRoot);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}
