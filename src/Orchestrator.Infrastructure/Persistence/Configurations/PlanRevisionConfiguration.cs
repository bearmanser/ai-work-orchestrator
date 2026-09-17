using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Plans;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class PlanRevisionConfiguration : IEntityTypeConfiguration<PlanRevision>
{
    public void Configure(EntityTypeBuilder<PlanRevision> b)
    {
        b.ToTable("plan_revisions");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.ContentHash).HasMaxLength(100).IsRequired();
        b.Property(r => r.ConfirmationStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(r => r.ContentJson).HasColumnType("jsonb").IsRequired();
        b.Ignore(r => r.Content);
        b.HasIndex(r => new { r.TaskId, r.RevisionNumber }).IsUnique();
        b.HasIndex(r => r.ContentHash);
    }
}
