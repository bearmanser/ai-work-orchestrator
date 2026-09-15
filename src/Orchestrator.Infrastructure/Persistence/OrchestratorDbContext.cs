using System.Text;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.Messaging;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Infrastructure.Persistence;

public class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options)
    : DbContext(options)
{
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orchestrator");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrchestratorDbContext).Assembly);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
            {
                // A uint concurrency token maps to PostgreSQL's "xmin" system column via
                // Npgsql's own model-finalizing convention, which runs after OnModelCreating
                // and only assigns "xmin" when no explicit column name has been configured.
                // Leave it untouched here - naming it ourselves (even "xmin") would make EF
                // treat it as an explicit override and, worse, would let a later rename
                // attempt an invalid ALTER TABLE ... RENAME COLUMN xmin TO ... against a
                // PostgreSQL system column.
                if (property.ClrType == typeof(uint) && property.IsConcurrencyToken)
                    continue;

                property.SetColumnName(ToSnakeCase(property.Name));
            }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
