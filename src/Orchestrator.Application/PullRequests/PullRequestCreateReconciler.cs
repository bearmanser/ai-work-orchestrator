using Orchestrator.Application.Gateway;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Application.PullRequests;

/// <summary>
/// Resolves an <see cref="ToolCallOutcome.Unknown"/> pull-request-create call by
/// checking whether a pull request already exists for the branch the call would
/// have opened one for. No production implementation of
/// <see cref="IOperationReconciler"/> existed before Task 49 - the acceptance
/// suite is what exposed that <c>OperationReconciliationService</c> always ran
/// with an empty reconciler list, so a crash between "Azure DevOps accepted the
/// create" and "the outcome was recorded" could never actually be reconciled;
/// every retry stayed permanently blocked by <see cref="UnreconciledOperationException"/>.
/// This is the minimal, well-scoped reconciler PullRequestCreationStepHandler's
/// own idempotency key format (<c>pr:{taskId}:{repositoryId}</c>) already
/// anticipates via its own <see cref="UnreconciledOperationException"/> catch
/// block that looks up the branch the same way.
/// </summary>
public sealed class PullRequestCreateReconciler(
    IRepositoryProfileStore profiles,
    IWorkspaceStore workspaces,
    IPullRequestHost host,
    IPullRequestTrackingStore tracking,
    TimeProvider clock) : IOperationReconciler
{
    public ActionKind Target => ActionKind.PullRequestCreate;

    public async Task<ReconciliationOutcome> ReconcileAsync(ToolCallRecord record, CancellationToken ct)
    {
        var parts = record.IdempotencyKey.Split(':');
        if (parts.Length != 3 || parts[0] != "pr" || !Guid.TryParse(parts[2], out var repositoryId))
            return ReconciliationOutcome.Indeterminate;

        var profile = await profiles.GetByIdAsync(repositoryId, ct);
        if (profile is null
            || !PullRequestCreationStepHandler.TryParseRemote(
                profile.RemoteUrl, out var organization, out var project, out var repositoryName))
            return ReconciliationOutcome.Indeterminate;

        var taskWorkspaces = await workspaces.ListForTaskAsync(record.TaskId, ct);
        var workspace = taskWorkspaces.FirstOrDefault(w => w.RepositoryId == repositoryId);
        if (workspace is null)
            return ReconciliationOutcome.Indeterminate;

        PullRequestRef? found;
        try
        {
            found = await host.FindOpenBySourceBranchAsync(
                organization, project, repositoryName, workspace.WorkBranch, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The remote itself could not be reached: this is exactly the case
            // reconciliation must not guess about.
            return ReconciliationOutcome.Indeterminate;
        }

        if (found is null)
            return ReconciliationOutcome.Failed;

        await tracking.UpsertAsync(new Domain.PullRequests.PullRequestTracking(
            record.TaskId, repositoryId, found.Id, found.Url,
            found.LastMergeSourceCommit, true, clock.GetUtcNow()), ct);
        return ReconciliationOutcome.Succeeded;
    }
}
