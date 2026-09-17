using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Authorization;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class ApprovalConfiguration : IEntityTypeConfiguration<Approval>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public void Configure(EntityTypeBuilder<Approval> b)
    {
        b.ToTable("approvals");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.ApprovalKind).HasMaxLength(100).IsRequired();
        b.Property(a => a.SubjectHash).HasMaxLength(200).IsRequired();
        b.Property(a => a.Action).HasConversion<string>().HasMaxLength(50).IsRequired();
        b.Property(a => a.ResourceDescription).HasMaxLength(1000).IsRequired();
        b.Property(a => a.ApproverId).HasMaxLength(200);
        b.Property(a => a.Decision).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(a => a.PolicyVersion).HasMaxLength(20).IsRequired();

        // Same pattern as AuthorizationEnvelopeConfiguration's string-list
        // properties: System.Text.Json materialises IReadOnlyList<string> as a
        // plain mutable List<string> on read, so it is re-wrapped immutable.
        b.Property(a => a.RequestedFrom)
            .HasConversion(
                list => JsonSerializer.Serialize(list, JsonOptions),
                json => (JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>())
                    .ToImmutableList())
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                (x, y) => (x ?? new List<string>()).SequenceEqual(y ?? new List<string>()),
                list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item!.GetHashCode())),
                list => list.ToList()));
        b.Property(a => a.RequestedFrom).HasColumnType("jsonb");

        b.HasIndex(a => a.TaskId);
    }
}
