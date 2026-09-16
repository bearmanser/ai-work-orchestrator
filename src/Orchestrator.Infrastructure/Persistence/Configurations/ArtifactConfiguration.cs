using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Evidence;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> b)
    {
        b.ToTable("artifacts");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).HasMaxLength(32).ValueGeneratedNever();
        b.Property(a => a.Type).HasMaxLength(100).IsRequired();
        b.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
        b.Property(a => a.Sensitivity).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(a => a.Producer).HasMaxLength(200).IsRequired();
        b.Property(a => a.ContentHash).HasMaxLength(100).IsRequired();
        b.HasIndex(a => new { a.TaskId, a.CreatedAt });
    }
}
