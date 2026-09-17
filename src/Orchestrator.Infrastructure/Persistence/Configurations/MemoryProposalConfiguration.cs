using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Memory;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class MemoryProposalConfiguration : IEntityTypeConfiguration<MemoryProposal>
{
    public void Configure(EntityTypeBuilder<MemoryProposal> b)
    {
        // MemoryProposal is a record: map the positional properties explicitly.
        b.ToTable("memory_proposals");
        b.HasKey("Id");
        b.Property<Guid>("Id").ValueGeneratedNever();
        b.Property(p => p.Content).HasMaxLength(8000).IsRequired();
        b.Property(p => p.CreatedAt).IsRequired();
        b.Property(p => p.VaultPath).HasMaxLength(500);
        b.Property(p => p.VaultRevisionId).HasMaxLength(100);
        b.Property(p => p.Evidence).HasColumnType("jsonb").IsRequired();
        b.HasIndex(p => p.TaskId);
    }
}
