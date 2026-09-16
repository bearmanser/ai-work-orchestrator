using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class RepositoryRelationshipConfiguration : IEntityTypeConfiguration<RepositoryRelationship>
{
    public void Configure(EntityTypeBuilder<RepositoryRelationship> b)
    {
        b.ToTable("repository_relationships");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.RelationshipType).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(r => r.EvidenceJson).HasColumnType("jsonb").IsRequired();
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(r => r.LastVerifiedCommit).HasMaxLength(100);
        b.Property(r => r.AdminNote).HasMaxLength(2000);
        b.Ignore(r => r.Evidence);
        b.HasIndex(r => r.SourceRepositoryId);
        b.HasIndex(r => r.TargetRepositoryId);
    }
}
