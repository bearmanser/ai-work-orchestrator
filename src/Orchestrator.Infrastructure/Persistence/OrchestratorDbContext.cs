using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
                // Identity-based escape hatch: a property whose column name has already
                // been configured explicitly (e.g. WorkTaskConfiguration's
                // .HasColumnName("xmin") for the row-version token, which maps to
                // PostgreSQL's "xmin" system column) is left untouched. This runs after
                // ApplyConfigurationsFromAssembly, so the explicit annotation is already
                // present by the time we get here. Renaming a system column like xmin
                // produces an invalid ALTER TABLE ... RENAME COLUMN at migration time, so
                // any future property that needs the same treatment can opt out the same
                // way - configure its column name explicitly - without this loop needing
                // to know why.
                if (property.FindAnnotation(RelationalAnnotationNames.ColumnName) is not null)
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
