using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class OperationalFlagConfiguration : IEntityTypeConfiguration<OperationalFlag>
{
    public void Configure(EntityTypeBuilder<OperationalFlag> b)
    {
        b.ToTable("operational_flags");
        b.HasKey(f => f.Key);
        b.Property(f => f.Key).HasMaxLength(100).ValueGeneratedNever();
    }
}
