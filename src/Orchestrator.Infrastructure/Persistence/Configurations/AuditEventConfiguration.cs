using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Audit;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Actor).HasMaxLength(200).IsRequired();
        b.Property(e => e.Action).HasMaxLength(200).IsRequired();
        b.Property(e => e.Reason).HasMaxLength(4000).IsRequired();
        b.Property(e => e.Outcome).HasMaxLength(50).IsRequired();
        b.Property(e => e.CorrelationId).HasMaxLength(300).IsRequired();
        b.Property(e => e.DetailsJson).HasColumnType("jsonb").IsRequired();
        b.HasIndex(e => new { e.TaskId, e.OccurredAt });
        b.HasIndex(e => new { e.Action, e.OccurredAt });
    }
}
