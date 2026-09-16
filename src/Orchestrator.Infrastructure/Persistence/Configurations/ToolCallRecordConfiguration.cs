using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class ToolCallRecordConfiguration : IEntityTypeConfiguration<ToolCallRecord>
{
    public void Configure(EntityTypeBuilder<ToolCallRecord> b)
    {
        b.ToTable("tool_call_records");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.TaskId).IsRequired();
        b.Property(r => r.StepId).IsRequired();
        b.Property(r => r.OperationId).HasMaxLength(200).IsRequired();
        b.Property(r => r.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(r => r.Outcome).IsRequired();
        b.Property(r => r.Decision).HasConversion<string>().HasMaxLength(50).IsRequired();
        b.Property(r => r.ResultJson).HasColumnType("jsonb");
        b.Property(r => r.RecordedAt).IsRequired();
        b.Property(r => r.CompletedAt);

        b.HasIndex(r => r.IdempotencyKey).IsUnique();
        b.HasIndex(r => r.TaskId);
        b.HasIndex(r => r.StepId);
    }
}