using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Humans;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class HumanQuestionConfiguration : IEntityTypeConfiguration<HumanQuestion>
{
    public void Configure(EntityTypeBuilder<HumanQuestion> b)
    {
        b.ToTable("human_questions");
        b.HasKey(q => q.Id);
        b.Property(q => q.Id).ValueGeneratedNever();
        b.Property(q => q.QuestionText).HasMaxLength(4000).IsRequired();
        b.Property(q => q.ChannelId).HasMaxLength(100).IsRequired();
        b.Property(q => q.ThreadTs).HasMaxLength(100).IsRequired();
        b.Property(q => q.PostedMessageTs).HasMaxLength(100).IsRequired();
        b.Property(q => q.AnswerText).HasMaxLength(4000);
        b.Property(q => q.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.HasIndex(q => new { q.ChannelId, q.ThreadTs });
        b.HasIndex(q => q.Status);
    }
}
