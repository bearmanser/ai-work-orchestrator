using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Messaging;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.MessageType).HasMaxLength(200).IsRequired();
        b.Property(m => m.PayloadJson).HasColumnType("jsonb").IsRequired();
        b.Property(m => m.IdempotencyKey).HasMaxLength(300).IsRequired();
        b.Property(m => m.LastError).HasMaxLength(4000);
        b.HasIndex(m => m.IdempotencyKey).IsUnique();
        b.HasIndex(m => new { m.ProcessedAt, m.NextAttemptAt });
    }
}
