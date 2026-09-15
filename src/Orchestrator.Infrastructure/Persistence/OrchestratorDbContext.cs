using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Infrastructure.Persistence;

public class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options)
    : DbContext(options)
{
    public DbSet<WorkTask> Tasks => Set<WorkTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orchestrator");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrchestratorDbContext).Assembly);
    }
}
