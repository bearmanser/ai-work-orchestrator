using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Identity;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class PersonIdentityConfiguration : IEntityTypeConfiguration<PersonIdentity>
{
    public void Configure(EntityTypeBuilder<PersonIdentity> b)
    {
        b.ToTable("people");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.DisplayName).HasMaxLength(300).IsRequired();
        b.Property(p => p.SlackUserId).HasMaxLength(100);
        b.Property(p => p.AzureDevOpsId).HasMaxLength(100);
        b.Property(p => p.Email).HasMaxLength(300);
        b.HasIndex(p => p.SlackUserId);
    }
}
