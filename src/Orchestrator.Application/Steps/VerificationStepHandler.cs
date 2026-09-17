using System.Text;
using System.Text.Json;
using Orchestrator.Application.Checks;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Steps;

/// <summary>
/// Verifies an implementation with runtime-owned checks: runs the profile's
/// commands itself, compares agent claims against observations, runs the
/// plan's P1 validations separately, and maps the outcome. Claims never
/// substitute for a run, and an empty diff never opens a PR.
/// </summary>
public sealed class VerificationStepHandler(
    IPlanningScopeStore scope,
    IPlanConfirmationStore confirmations,
    IRepositoryProfileStore profiles,
    IWorkspaceStore workspaces,
    IGitWorkspace git,
    CheckRunner checks,
    IDomainDataSource p1,
    IAgentRunStore runs,
    IArtifactStore artifacts,
    IAuditWriter audit) : IStepHandler
{
    public string Capability => "verification";

    public async Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
    {
        var task = await scope.GetTaskAsync(context.TaskId, ct);
        var ledger = await scope.GetLedgerAsync(context.TaskId, ct);
        if (ledger is not null)
        {
            try
            {
                ledger.EnsureWithinLimits();
            }
            catch (BudgetExceededException ex)
            {
                return new StepOutcome(TaskTrigger.UnrecoverableFailure, null, ex.Message);
            }

            if (ledger.CorrectionAttempts >= ledger.Limits.MaxCorrectionAttempts)
            {
                return new StepOutcome(TaskTrigger.UnrecoverableFailure, null,
                    "Correction attempts exhausted; failing rather than looping.");
            }
        }

        var revision = await LatestConfirmedRevisionAsync(context.TaskId, ct);
        if (revision is null)
        {
            return new StepOutcome(null, null,
                "There is no confirmed plan for this task; verification requires one.");
        }

        var runtimeChecks = new List<CheckResult>();
        foreach (var change in revision.Content.RepositoryChanges.Where(c => c.Intent == ChangeIntent.Change))
        {
            var workspace = (await workspaces.ListForTaskAsync(context.TaskId, ct))
                .FirstOrDefault(w => w.RepositoryId == change.RepositoryId);
            if (workspace is null)
                continue;

            var profile = await profiles.GetByIdAsync(change.RepositoryId, ct);
            if (profile is null)
                continue;

            runtimeChecks.AddRange(await checks.RunAsync(task.Id, profile, workspace.Path, ct));
        }

        var domainChecks = new List<DomainCheckResult>();
        var envelope = await scope.GetEnvelopeAsync(context.TaskId, ct);
        foreach (var queryName in revision.Content.P1Checks)
        {
            try
            {
                domainChecks.Add(await p1.ExecuteNamedQueryAsync(new DomainQueryRequest(
                    queryName,
                    envelope?.P1Environments.FirstOrDefault() ?? "staging",
                    envelope?.P1Companies.FirstOrDefault() ?? string.Empty,
                    new Dictionary<string, string>()), ct));
            }
            catch (Exception ex)
            {
                domainChecks.Add(new DomainCheckResult(
                    queryName,
                    envelope?.P1Environments.FirstOrDefault() ?? "staging",
                    envelope?.P1Companies.FirstOrDefault() ?? string.Empty,
                    false, [], $"Query failed: {ex.Message}"));
            }
        }

        var claims = await AgentClaimsAsync(context.TaskId, ct);
        var comparison = CheckClaimComparer.Compare(claims, runtimeChecks);
        var needsCorrection = false;
        if (comparison.HasMismatch)
        {
            needsCorrection = true;
            await audit.RecordAsync(new AuditEntry(
                Actor: "orchestrator",
                TaskId: task.Id,
                Action: "checks.claim-mismatch",
                Reason: string.Join("; ", comparison.Mismatches),
                Outcome: "Mismatch",
                CorrelationId: context.StepId.ToString(),
                Details: new Dictionary<string, string>()), ct);
        }

        var diff = await CaptureDiffsAsync(context.TaskId, revision, ct);
        if (runtimeChecks.Count == 0 && !diff.AllEmpty)
        {
            // Nothing was actually run: claims can never substitute for a run.
            needsCorrection = true;
        }

        var evidence = new TaskEvidence(
            runtimeChecks,
            domainChecks,
            new GitDiffSummary(diff.Baseline, diff.Head, diff.Files, diff.EvidenceArtifactId),
            revision.ContentHash,
            []);
        var evidenceId = await artifacts.PutAsync(
            new ArtifactDescriptor(task.Id, "task-evidence", "application/json",
                ArtifactSensitivity.Internal, "verification-step"),
            new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evidence))), ct);

        if (diff.AllEmpty)
            return new StepOutcome(TaskTrigger.NonCodeOutcomeAccepted, evidenceId, null);

        if (needsCorrection
            || runtimeChecks.Any(c => !c.Passed)
            || domainChecks.Any(c => !c.Passed))
        {
            if (ledger is not null)
            {
                ledger.RecordCorrectionAttempt();
                await scope.SaveLedgerAsync(ledger, ct);
            }

            return new StepOutcome(TaskTrigger.CorrectionRequired, evidenceId, null);
        }

        return new StepOutcome(TaskTrigger.PullRequestOpened, evidenceId, null);
    }

    private async Task<PlanRevision?> LatestConfirmedRevisionAsync(Guid taskId, CancellationToken ct)
    {
        var revisions = await confirmations.ListRevisionsAsync(taskId, ct);
        return revisions
            .Where(r => r.ConfirmationStatus == ConfirmationStatus.Confirmed)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<ClaimedCheck>> AgentClaimsAsync(Guid taskId, CancellationToken ct)
    {
        var claims = new List<ClaimedCheck>();
        foreach (var run in await runs.ListForTaskAsync(taskId, ct))
        {
            try
            {
                var result = JsonSerializer.Deserialize<AgentResult>(run.ResultJson);
                if (result?.ClaimedChecks is not null)
                    claims.AddRange(result.ClaimedChecks);
            }
            catch (JsonException)
            {
                // An unreadable agent record contributes no claims.
            }
        }

        return claims;
    }

    private async Task<(bool AllEmpty, string Baseline, string Head, List<string> Files, string? EvidenceArtifactId)> CaptureDiffsAsync(
        Guid taskId, PlanRevision revision, CancellationToken ct)
    {
        var files = new List<string>();
        var baseline = revision.Content.RepositoryChanges.FirstOrDefault()?.BaselineCommit ?? string.Empty;
        var head = baseline;
        var empty = true;
        string? evidenceArtifactId = null;

        foreach (var workspace in await workspaces.ListForTaskAsync(taskId, ct))
        {
            GitDiff diff;
            try
            {
                diff = await git.CaptureDiffAsync(workspace.WorkspaceId, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                continue;
            }

            if (diff.Files.Count > 0)
            {
                empty = false;
                head = diff.HeadCommit;
                files.AddRange(diff.Files.Select(f => f.Path));
            }

            evidenceArtifactId ??= await artifacts.PutAsync(
                new ArtifactDescriptor(taskId, "diff", "text/plain",
                    ArtifactSensitivity.Internal, "verification-step"),
                new MemoryStream(Encoding.UTF8.GetBytes(diff.UnifiedDiff)), ct);
        }

        return (empty, baseline, head, files, evidenceArtifactId);
    }
}
