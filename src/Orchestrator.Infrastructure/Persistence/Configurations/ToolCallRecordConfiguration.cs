using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class ToolCallRecordConfiguration : IEntityTypeConfiguration<ToolCallRecord>
{
    public void Configure(EntityTypeBuilder<ToolCallRecord> b)
    {
        b.ToTable("tool_call_records");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.TaskId).IsRequired();
        b.Property(r => r.StepId).IsRequired();
        b.Property(r => r.Action).HasConversion<string>().HasMaxLength(50).IsRequired();
        b.Property(r => r.OperationId).HasMaxLength(200).IsRequired();
        b.Property(r => r.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(r => r.Outcome).IsRequired();

        // PolicyDecision is a closed hierarchy carrying its reason (and, for
        // ApprovalRequired, an approval kind) - not a bare enum - so it needs an
        // explicit converter rather than the `HasConversion<string>()` shorthand,
        // which only handles types with a built-in string representation.
        b.Property(r => r.Decision)
            .HasConversion(d => Serialize(d), s => Deserialize(s))
            .HasColumnType("jsonb")
            .IsRequired();

        b.Property(r => r.ResultJson).HasColumnType("jsonb");
        b.Property(r => r.RecordedAt).IsRequired();
        b.Property(r => r.CompletedAt);

        b.HasIndex(r => r.IdempotencyKey).IsUnique();
        b.HasIndex(r => r.TaskId);
        b.HasIndex(r => r.StepId);
    }

    private static string Serialize(PolicyDecision decision) => JsonSerializer.Serialize(decision switch
    {
        PolicyDecision.Permitted p => new DecisionDto("Permitted", p.Reason, null),
        PolicyDecision.Denied d => new DecisionDto("Denied", d.Reason, null),
        PolicyDecision.ApprovalRequired a => new DecisionDto("ApprovalRequired", a.Reason, a.ApprovalKind),
        _ => throw new InvalidOperationException($"Unknown policy decision type: {decision.GetType()}."),
    });

    private static PolicyDecision Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<DecisionDto>(json)
            ?? throw new InvalidOperationException("Stored policy decision could not be deserialized.");
        return dto.Kind switch
        {
            "Permitted" => PolicyDecision.Permit(dto.Reason),
            "Denied" => PolicyDecision.Deny(dto.Reason),
            "ApprovalRequired" => PolicyDecision.RequireApproval(dto.Reason, dto.ApprovalKind!),
            _ => throw new InvalidOperationException($"Unknown stored policy decision kind: {dto.Kind}."),
        };
    }

    private sealed record DecisionDto(string Kind, string Reason, string? ApprovalKind);
}
