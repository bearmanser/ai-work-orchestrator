using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Identity;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class OwnershipEntryConfiguration : IEntityTypeConfiguration<OwnershipEntry>
{
    public void Configure(EntityTypeBuilder<OwnershipEntry> b)
    {
        b.ToTable("ownership_entries");
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).ValueGeneratedNever();
        b.Property(o => o.Subject).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(o => o.SubjectKey).HasMaxLength(200).IsRequired();
        b.HasIndex(o => new { o.Subject, o.SubjectKey });
    }
}
