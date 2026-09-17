using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Policy;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class PolicyConfigurationRowConfiguration : IEntityTypeConfiguration<PolicyConfigurationRow>
{
    public void Configure(EntityTypeBuilder<PolicyConfigurationRow> b)
    {
        b.ToTable("policy_configuration");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.AlwaysRequiresApprovalCsv).HasMaxLength(500).IsRequired();
        b.Property(r => r.ForbiddenEnvironmentsCsv).HasMaxLength(500).IsRequired();
    }
}
