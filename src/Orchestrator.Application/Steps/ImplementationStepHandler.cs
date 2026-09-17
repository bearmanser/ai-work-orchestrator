using System.Text;
using System.Text.Json;
using Orchestrator.Application.Agents;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Steps;

/// <summary>
/// Runs the confirmed plan: drift-guard, budget-guard, provider-scope guard,
/// one workspace per change target, one agent run per workspace, budget charge,
/// and a diff artifact even when the agent fails.
/// </summary>
public sealed class ImplementationStepHandler(
    IPlanningScopeStore scope,
    IPlanConfirmationStore confirmations,
    IRepositoryProfileStore profiles,
    IGitWorkspace git,
    AgentRouter router,
    IArtifactStore artifacts,
    IAgentRunStore runs,
    TimeProvider clock) : IStepHandler
{
    public string Capability => "coding_agent.execute";

    public async Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
    {
        var task = await scope.GetTaskAsync(context.TaskId, ct);
        var revision = await LatestConfirmedRevisionAsync(context.TaskId, ct);
        if (revision is null)
        {
            return new StepOutcome(null, null,
                "There is no confirmed plan for this task; implementation requires one.");
        }

        if (await FindDriftedRepositoryAsync(revision.Content, ct) is { } drifted)
        {
            return new StepOutcome(TaskTrigger.BaselineDriftDetected, null,
                $"Repository '{drifted}' baseline moved since confirmation.");
        }

        var ledger = await scope.GetLedgerAsync(context.TaskId, ct);
        try
        {
            ledger?.EnsureWithinLimits();
        }
        catch (BudgetExceededException ex)
        {
            return new StepOutcome(TaskTrigger.UnrecoverableFailure, null, ex.Message);
        }

        var envelope = await scope.GetEnvelopeAsync(context.TaskId, ct);
        if (envelope is null)
        {
            return new StepOutcome(null, null,
                $"Task {context.TaskId} has no authorization envelope.");
        }

        var unauthorized = revision.Content.Providers
            .Where(p => !envelope.AllowedProviders.Contains(p, StringComparer.Ordinal))
            .ToList();
        if (unauthorized.Count > 0)
        {
            return new StepOutcome(null, null,
                $"Planned provider '{unauthorized.First()}' is not in the task's authorized providers.");
        }

        foreach (var change in revision.Content.RepositoryChanges.Where(c => c.Intent == ChangeIntent.Change))
        {
            var profile = await profiles.GetByIdAsync(change.RepositoryId, ct);
            if (profile is null)
                continue;

            var handle = await git.PrepareAsync(new WorkspaceRequest(
                task.Id, change.RepositoryId, profile.RemoteUrl, profile.DefaultBranch,
                $"orchestrator/{task.Id:N}", new SecretRef($"git.{profile.Name.ToLowerInvariant()}.credential")), ct);

            var request = new AgentRequest(
                task.Id,
                Guid.CreateVersion7().ToString("N"),
                AgentStage.Implementation,
                new TrustedInstructions(
                    revision.Content.Objective,
                    revision.Content.Tests,
                    revision.Content.Scope,
                    revision.Content.Exclusions),
                new EvidenceBundle([], [], []),
                handle,
                new ProviderPolicy(revision.Content.Providers, FallbackAllowed: false),
                envelope.Budget,
                envelope.Id.ToString());

            var result = await router.ExecuteAsync(request, ct);

            await runs.AddAsync(new AgentRun(
                task.Id, context.StepId, change.RepositoryId,
                JsonSerializer.Serialize(result), clock.GetUtcNow()), ct);

            if (ledger is not null)
            {
                ledger.Charge(result.Usage.CostUsd, result.Usage.Duration, 1);
                await scope.SaveLedgerAsync(ledger, ct);
            }

            await CaptureDiffAsync(task.Id, handle.WorkspaceId, ct);

            if (result.Status != AgentStatus.Succeeded)
            {
                return new StepOutcome(null, null,
                    $"Agent implementation failed: {result.FailureDetail ?? result.Status.ToString()}.");
            }
        }

        return new StepOutcome(TaskTrigger.ImplementationFinished, null, null);
    }

    private async Task<PlanRevision?> LatestConfirmedRevisionAsync(Guid taskId, CancellationToken ct)
    {
        var revisions = await confirmations.ListRevisionsAsync(taskId, ct);
        return revisions
            .Where(r => r.ConfirmationStatus == ConfirmationStatus.Confirmed)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefault();
    }

    private async Task<string?> FindDriftedRepositoryAsync(PlanContent content, CancellationToken ct)
    {
        foreach (var change in content.RepositoryChanges)
        {
            var profile = await profiles.GetByIdAsync(change.RepositoryId, ct);
            var current = profile?.LastInspectedCommit;
            if (current is not null
                && !string.Equals(current, change.BaselineCommit, StringComparison.Ordinal))
            {
                return change.RepositoryName;
            }
        }

        return null;
    }

    private async Task CaptureDiffAsync(Guid taskId, string workspaceId, CancellationToken ct)
    {
        try
        {
            var diff = await git.CaptureDiffAsync(workspaceId, ct);
            await artifacts.PutAsync(
                new ArtifactDescriptor(taskId, "diff", "text/plain",
                    ArtifactSensitivity.Internal, "implementation-step"),
                new MemoryStream(Encoding.UTF8.GetBytes(diff.UnifiedDiff)), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Best effort: the failure outcome matters more than the artifact.
        }
    }
}
