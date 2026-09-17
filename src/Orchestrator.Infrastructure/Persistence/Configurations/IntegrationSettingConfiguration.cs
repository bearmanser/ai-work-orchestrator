using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Integrations;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class IntegrationSettingConfiguration : IEntityTypeConfiguration<IntegrationSetting>
{
    public void Configure(EntityTypeBuilder<IntegrationSetting> b)
    {
        b.ToTable("integration_settings");
        b.HasKey(r => r.Name);
        b.Property(r => r.Name).HasMaxLength(100).ValueGeneratedNever();
        b.Property(r => r.SecretRef).HasMaxLength(200);
        b.Property(r => r.Health).HasMaxLength(50);
        b.Property(r => r.Detail).HasMaxLength(2000);
    }
}
