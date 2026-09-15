using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Messaging;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class InboundEventConfiguration : IEntityTypeConfiguration<InboundEvent>
{
    public void Configure(EntityTypeBuilder<InboundEvent> b)
    {
        b.ToTable("inbound_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Source).HasMaxLength(50).IsRequired();
        b.Property(e => e.ExternalEventId).HasMaxLength(300).IsRequired();
        b.Property(e => e.PayloadHash).HasMaxLength(100).IsRequired();
        b.HasIndex(e => new { e.Source, e.ExternalEventId }).IsUnique();
    }
}
