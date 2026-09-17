using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.PullRequests;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class PullRequestTrackingConfiguration : IEntityTypeConfiguration<PullRequestTracking>
{
    public void Configure(EntityTypeBuilder<PullRequestTracking> b)
    {
        b.ToTable("pull_request_tracking");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.Url).HasMaxLength(1000).IsRequired();
        b.Property(t => t.CurrentHeadCommit).HasMaxLength(100).IsRequired();
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.HasIndex(t => t.TaskId);
        b.HasIndex(t => new { t.TaskId, t.RepositoryId }).IsUnique();

        b.OwnsMany(t => t.Feedback, o =>
        {
            o.ToJson();
            o.HasAnnotation("Orchestrator:SkipSnakeCase", true);
            o.Property(f => f.Text).HasMaxLength(4000);
        });
    }
}
