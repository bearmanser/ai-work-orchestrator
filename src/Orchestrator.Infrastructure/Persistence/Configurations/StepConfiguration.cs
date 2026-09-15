using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Steps;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class StepConfiguration : IEntityTypeConfiguration<Step>
{
    public void Configure(EntityTypeBuilder<Step> b)
    {
        b.ToTable("steps");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.Capability).HasMaxLength(200).IsRequired();
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(s => s.InputArtifactId).HasMaxLength(300);
        b.Property(s => s.OutputArtifactId).HasMaxLength(300);
        b.Property(s => s.LeaseOwner).HasMaxLength(200);
        b.HasIndex(s => new { s.TaskId, s.Status });
        b.HasIndex(s => new { s.Status, s.LeaseExpiresAt });
    }
}
