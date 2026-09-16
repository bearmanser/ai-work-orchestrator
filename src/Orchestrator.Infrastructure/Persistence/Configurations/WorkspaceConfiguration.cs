using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.ToTable("workspaces");
        b.HasKey(w => w.WorkspaceId);
        b.Property(w => w.WorkspaceId).HasMaxLength(32).ValueGeneratedNever();
        b.Property(w => w.Path).HasMaxLength(1000).IsRequired();
        b.Property(w => w.BaselineCommit).HasMaxLength(100).IsRequired();
        b.Property(w => w.BaseBranch).HasMaxLength(200).IsRequired();
        b.Property(w => w.WorkBranch).HasMaxLength(200).IsRequired();
        b.Property(w => w.CredentialKey).HasMaxLength(200).IsRequired();
        b.HasIndex(w => new { w.TaskId, w.RepositoryId }).IsUnique();
        b.HasIndex(w => w.TaskId);
    }
}
