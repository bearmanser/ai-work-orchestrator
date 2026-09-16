using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orchestrator.Domain.Budgets;

namespace Orchestrator.Infrastructure.Persistence.Configurations;

public sealed class BudgetLedgerConfiguration : IEntityTypeConfiguration<BudgetLedger>
{
    public void Configure(EntityTypeBuilder<BudgetLedger> b)
    {
        b.ToTable("budgets");
        b.HasKey(l => l.Id);
        b.Property(l => l.Id).ValueGeneratedNever();
        b.HasIndex(l => l.TaskId).IsUnique();

        // 18 total digits, 6 after the decimal point: comfortably covers agent costs
        // quoted to four or more decimals (e.g. a Claude result's total_cost_usd of
        // 0.4213) plus cumulative task spend, and states the column's contract
        // explicitly rather than leaving it as unscaled numeric.
        b.Property(l => l.SpentUsd).HasPrecision(18, 6);

        // BudgetLimits is table-split onto the same "budgets" row as its owner. EF's
        // default table-splitting convention gives the owned type a shadow FK/PK
        // property named "BudgetLedgerId" that shares the owner's key *value* but not
        // automatically its *column name* once the snake_case convention in
        // OrchestratorDbContext has renamed each side independently - "id" for
        // BudgetLedger.Id versus "budget_ledger_id" for the shadow property - which EF
        // then rejects as two different columns backing the same primary key. Pinning
        // the shadow property's column name to "id" here keeps both sides of the
        // shared key on the same column; that explicit ColumnName annotation is also
        // what makes the DbContext's snake_case loop skip it instead of renaming it
        // again. The four BudgetLimits value columns are left to the automatic
        // snake_case convention: max_usd / max_wall_clock / max_agent_invocations /
        // max_correction_attempts.
        b.OwnsOne(l => l.Limits, o =>
        {
            o.WithOwner().HasForeignKey("BudgetLedgerId");
            o.Property<Guid>("BudgetLedgerId").HasColumnName("id");
            o.HasKey("BudgetLedgerId");
            o.Property(l => l.MaxUsd).HasPrecision(18, 6);
        });
        b.Navigation(l => l.Limits).IsRequired();
    }
}
