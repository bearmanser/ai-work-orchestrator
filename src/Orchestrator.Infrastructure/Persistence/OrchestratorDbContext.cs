using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Orchestrator.Domain.Audit;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Context;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Integrations;
using Orchestrator.Domain.Memory;
using Orchestrator.Domain.Messaging;
using Orchestrator.Domain.Operations;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Policy;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Infrastructure.Persistence;

public class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options)
    : DbContext(options)
{
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboundEvent> InboundEvents => Set<InboundEvent>();
    public DbSet<MemoryProposal> MemoryProposals => Set<MemoryProposal>();
    public DbSet<PlanRevision> PlanRevisions => Set<PlanRevision>();
    public DbSet<OperationalFlag> OperationalFlags => Set<OperationalFlag>();
    public DbSet<Step> Steps => Set<Step>();
    public DbSet<PullRequestTracking> PullRequestTracking => Set<PullRequestTracking>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<BudgetLedger> Budgets => Set<BudgetLedger>();
    public DbSet<AuthorizationEnvelope> AuthorizationEnvelopes => Set<AuthorizationEnvelope>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ContextSnapshot> ContextSnapshots => Set<ContextSnapshot>();
    public DbSet<AdminAccount> AdminAccounts => Set<AdminAccount>();
    public DbSet<RepositoryProfile> Repositories => Set<RepositoryProfile>();
    public DbSet<RepositoryRelationship> Relationships => Set<RepositoryRelationship>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<HumanQuestion> HumanQuestions => Set<HumanQuestion>();
    public DbSet<ToolCallRecord> ToolCallRecords => Set<ToolCallRecord>();
    public DbSet<PolicyConfigurationRow> PolicyConfigurations => Set<PolicyConfigurationRow>();
    public DbSet<IntegrationSetting> IntegrationSettings => Set<IntegrationSetting>();
    public DbSet<PersonIdentity> People => Set<PersonIdentity>();
    public DbSet<OwnershipEntry> OwnershipEntries => Set<OwnershipEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orchestrator");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrchestratorDbContext).Assembly);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Properties of JSON-mapped owned types (e.g. the feedback items stored
            // inside a pull request tracking row) take JSON property names, not
            // column names; giving them column names breaks model building. The
            // owning configuration marks such types with this annotation.
            if (entity.FindAnnotation("Orchestrator:SkipSnakeCase") is not null)
                continue;

            foreach (var property in entity.GetProperties())
            {
                // Identity-based escape hatch: a property whose column name has already
                // been configured explicitly (e.g. WorkTaskConfiguration's
                // .HasColumnName("xmin") for the row-version token, which maps to
                // PostgreSQL's "xmin" system column) is left untouched. This runs after
                // ApplyConfigurationsFromAssembly, so the explicit annotation is already
                // present by the time we get here. Renaming a system column like xmin
                // produces an invalid ALTER TABLE ... RENAME COLUMN at migration time, so
                // any future property that needs the same treatment can opt out the same
                // way - configure its column name explicitly - without this loop needing
                // to know why.
                if (property.FindAnnotation(RelationalAnnotationNames.ColumnName) is not null)
                    continue;

                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
