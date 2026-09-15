using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orchestrator.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ORCHESTRATOR_DESIGNTIME_CONNECTION")
                 ?? "Host=localhost;Database=orchestrator_design;Username=postgres;Password=postgres";
        return new OrchestratorDbContext(
            new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(cs).Options);
    }
}
