using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Context;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class ContextSnapshotConfiguration : IEntityTypeConfiguration<ContextSnapshot>
{
    public void Configure(EntityTypeBuilder<ContextSnapshot> b)
    {
        b.ToTable("context_snapshots");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.ArtifactId).HasMaxLength(32).IsRequired();
        b.Property(s => s.SourcesJson).HasColumnType("jsonb").IsRequired();
        b.Property(s => s.AccessScope).HasMaxLength(4000).IsRequired();
        b.Property(s => s.OmittedSourcesJson).HasColumnType("jsonb").IsRequired();
        b.HasIndex(s => new { s.TaskId, s.RetrievedAt });
    }
}
