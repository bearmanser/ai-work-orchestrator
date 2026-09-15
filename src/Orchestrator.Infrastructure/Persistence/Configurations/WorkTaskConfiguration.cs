using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class WorkTaskConfiguration : IEntityTypeConfiguration<WorkTask>
{
    public void Configure(EntityTypeBuilder<WorkTask> b)
    {
        b.ToTable("tasks");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.Objective).HasMaxLength(4000).IsRequired();
        b.Property(t => t.RequesterId).HasMaxLength(200).IsRequired();
        b.Property(t => t.CorrelationId).HasMaxLength(300).IsRequired();
        b.Property(t => t.State).HasConversion<string>().HasMaxLength(60).IsRequired();
        b.Property(t => t.Source).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(t => t.Version).IsRowVersion(); // maps to xmin on PostgreSQL
        b.HasIndex(t => t.CorrelationId).IsUnique();
        b.HasIndex(t => t.State);
    }
}
