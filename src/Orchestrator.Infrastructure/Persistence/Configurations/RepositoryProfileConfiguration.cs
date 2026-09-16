using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class RepositoryProfileConfiguration : IEntityTypeConfiguration<RepositoryProfile>
{
    public void Configure(EntityTypeBuilder<RepositoryProfile> b)
    {
        b.ToTable("repositories");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.Name).HasMaxLength(200).IsRequired();
        b.Property(r => r.RemoteUrl).HasMaxLength(500).IsRequired();
        b.Property(r => r.DefaultBranch).HasMaxLength(200).IsRequired();
        b.Property(r => r.Purpose).HasMaxLength(2000).IsRequired();
        b.Property(r => r.OwnerContactId).HasMaxLength(200).IsRequired();
        b.Property(r => r.AccessPolicy).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(r => r.InstructionFilePathsJson).HasColumnType("jsonb").IsRequired();
        b.Property(r => r.LastInspectedCommit).HasMaxLength(100);
        b.Property(r => r.Version).IsRowVersion().HasColumnName("xmin"); // PostgreSQL system column
        b.HasIndex(r => r.RemoteUrl).IsUnique();
        b.HasIndex(r => r.Name);

        // BuildCommands is table-split onto the same "repositories" row; see
        // BudgetLedgerConfiguration for why the shared key column is pinned.
        b.OwnsOne(r => r.BuildCommands, o =>
        {
            o.WithOwner().HasForeignKey("RepositoryProfileId");
            o.Property<Guid>("RepositoryProfileId").HasColumnName("id");
            o.HasKey("RepositoryProfileId");
            o.Property(c => c.Restore).HasMaxLength(500);
            o.Property(c => c.Build).HasMaxLength(500);
            o.Property(c => c.Test).HasMaxLength(500);
            o.Property(c => c.Lint).HasMaxLength(500);
            o.Property(c => c.WorkingDirectory).HasMaxLength(500).IsRequired();
        });
        b.Navigation(r => r.BuildCommands).IsRequired();
    }
}
