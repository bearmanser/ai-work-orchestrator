using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Authorization;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class AuthorizationEnvelopeConfiguration : IEntityTypeConfiguration<AuthorizationEnvelope>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public void Configure(EntityTypeBuilder<AuthorizationEnvelope> b)
    {
        b.ToTable("authorization_envelopes");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.HasIndex(e => e.TaskId);

        b.Property(e => e.RequesterId).HasMaxLength(200).IsRequired();

        ConfigureStringList(b.Property(e => e.P1Environments));
        ConfigureStringList(b.Property(e => e.P1Companies));
        ConfigureStringList(b.Property(e => e.AllowedProviders));
        ConfigureStringList(b.Property(e => e.SlackChannels));
        ConfigureStringList(b.Property(e => e.ContactablePeople));
        ConfigureStringList(b.Property(e => e.VaultPathPrefixes));
        ConfigureStringList(b.Property(e => e.NetworkDestinations));

        b.Property(e => e.AllowedStages)
            .HasConversion(
                stages => JsonSerializer.Serialize(stages, JsonOptions),
                json => JsonSerializer.Deserialize<List<AgentStage>>(json, JsonOptions) ?? new List<AgentStage>())
            .Metadata.SetValueComparer(ListComparer<AgentStage>());
        b.Property(e => e.AllowedStages).HasColumnType("jsonb");

        b.Property(e => e.Repositories)
            .HasConversion(
                repos => JsonSerializer.Serialize(repos, JsonOptions),
                json => JsonSerializer.Deserialize<List<RepositoryScope>>(json, JsonOptions) ?? new List<RepositoryScope>())
            .Metadata.SetValueComparer(ListComparer<RepositoryScope>());
        b.Property(e => e.Repositories).HasColumnType("jsonb");

        // BudgetLimits is table-split onto the same "authorization_envelopes" row as
        // its owner, mirroring BudgetLedgerConfiguration exactly: the owned type's
        // shadow FK/PK property needs its column name pinned to "id" so it lands on
        // the same physical column as AuthorizationEnvelope.Id once the snake_case
        // convention has renamed each side independently, and MaxUsd needs the same
        // HasPrecision(18, 6) as the ledger's copy of BudgetLimits - EF owned-type
        // configuration is per-owner, not per-type, so the precision does not carry
        // over from BudgetLedgerConfiguration and must be repeated here.
        b.OwnsOne(e => e.Budget, o =>
        {
            o.WithOwner().HasForeignKey("AuthorizationEnvelopeId");
            o.Property<Guid>("AuthorizationEnvelopeId").HasColumnName("id");
            o.HasKey("AuthorizationEnvelopeId");
            o.Property(l => l.MaxUsd).HasPrecision(18, 6);
        });
        b.Navigation(e => e.Budget).IsRequired();
    }

    private static void ConfigureStringList(PropertyBuilder<IReadOnlyList<string>> property)
    {
        property.HasConversion(
            list => JsonSerializer.Serialize(list, JsonOptions),
            json => JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>());
        property.Metadata.SetValueComparer(ListComparer<string>());
        property.HasColumnType("jsonb");
    }

    private static ValueComparer<IReadOnlyList<T>> ListComparer<T>() => new(
        (a, b) => (a ?? new List<T>()).SequenceEqual(b ?? new List<T>()),
        list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item!.GetHashCode())),
        list => list.ToList());
}
