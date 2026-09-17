using Orchestrator.Application.Context;
using Orchestrator.Application.Evidence;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.PullRequests;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Plans;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Steps;

/// <summary>
/// Opens one pull request per changed repository with a non-empty diff: pushes
/// the work branch, adopts (and refreshes) an already-open PR for the branch
/// instead of duplicating it, otherwise creates through the tool gateway, and
/// links every PR to every other required PR in a second pass. A crash
/// mid-creation leaves an <c>Unknown</c> gateway outcome, which forces the next
/// attempt into reconciliation rather than a second POST.
/// </summary>
public sealed class PullRequestCreationStepHandler(
    IPlanningScopeStore scope,
    IPlanConfirmationStore confirmations,
    IRepositoryProfileStore profiles,
    IWorkspaceStore workspaces,
    IGitWorkspace git,
    IPullRequestHost host,
    IToolGateway gateway,
    EvidenceAssembler assembler,
    IPullRequestTrackingStore tracking,
    OwnershipDirectory ownership,
    IAuditWriter audit,
    TimeProvider clock) : IStepHandler
{
    public string Capability => "pull_request.create";

    public async Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
    {
        var task = await scope.GetTaskAsync(context.TaskId, ct);
        var revision = await LatestConfirmedRevisionAsync(context.TaskId, ct);
        if (revision is null)
        {
            return new StepOutcome(null, null,
                "There is no confirmed plan for this task; PR creation requires one.");
        }

        var envelope = await scope.GetEnvelopeAsync(context.TaskId, ct);
        if (envelope is null)
        {
            return new StepOutcome(null, null,
                $"Task {context.TaskId} has no authorization envelope.");
        }

        var ledger = await scope.GetLedgerAsync(context.TaskId, ct);
        if (ledger is null)
        {
            return new StepOutcome(null, null,
                $"Task {context.TaskId} has no budget ledger.");
        }

        var evidence = await assembler.AssembleAsync(task.Id, ct);
        var created = new List<PullRequestRef>();

        foreach (var change in revision.Content.RepositoryChanges.Where(c => c.Intent == ChangeIntent.Change))
        {
            var workspace = (await workspaces.ListForTaskAsync(task.Id, ct))
                .FirstOrDefault(w => w.RepositoryId == change.RepositoryId);
            if (workspace is null)
            {
                return Failure($"No workspace exists for repository '{change.RepositoryName}'.");
            }

            GitDiff diff;
            try
            {
                diff = await git.CaptureDiffAsync(workspace.WorkspaceId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return Failure($"Could not capture the diff for '{change.RepositoryName}': {ex.Message}");
            }

            if (diff.Files.Count == 0)
                continue;

            var profile = await profiles.GetByIdAsync(change.RepositoryId, ct);
            if (profile is null
                || !TryParseRemote(profile.RemoteUrl, out var organization, out var project, out var repositoryName))
            {
                return Failure($"Repository '{change.RepositoryName}' has no parseable Azure DevOps remote.");
            }

            try
            {
                await git.PushAsync(workspace.WorkspaceId, new SecretRef(workspace.CredentialKey), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return new StepOutcome(null, null,
                    $"Push of '{change.RepositoryName}' was rejected: {ex.Message}");
            }

            var reviewers = await ReviewerIdsAsync(change.RepositoryId, ct);
            var existing = await host.FindOpenBySourceBranchAsync(
                organization, project, repositoryName, workspace.WorkBranch, ct);
            if (existing is not null)
            {
                await host.UpdateDescriptionAsync(existing.Id,
                    PullRequestDescriptionBuilder.Build(evidence, revision, []), ct);
                await tracking.UpsertAsync(new PullRequestTracking(
                    task.Id, change.RepositoryId, existing.Id, existing.Url,
                    diff.HeadCommit, true, clock.GetUtcNow()), ct);
                created.Add(existing);
                continue;
            }

            var description = PullRequestDescriptionBuilder.Build(evidence, revision, []);
            var call = new ToolCall(
                task.Id, context.StepId, "orchestrator", ActionKind.PullRequestCreate,
                ResourceScope.Repository(change.RepositoryId, workspace.WorkBranch),
                Guid.CreateVersion7().ToString("N"), $"pr:{task.Id}:{change.RepositoryId}",
                $"Create pull request for '{change.RepositoryName}'.",
                envelope, ledger);

            PullRequestRef createdRef;
            try
            {
                var result = await gateway.InvokeAsync(call,
                    c => host.CreateAsync(new CreatePullRequest(
                        organization, project, repositoryName, workspace.WorkBranch, profile.DefaultBranch,
                        revision.Content.Objective, description, reviewers), c), ct);
                createdRef = result.Value
                    ?? throw new InvalidOperationException($"Gateway returned no pull request for '{change.RepositoryName}'.");
            }
            catch (UnreconciledOperationException)
            {
                var found = await host.FindOpenBySourceBranchAsync(
                    organization, project, repositoryName, workspace.WorkBranch, ct);
                if (found is null)
                {
                    return Failure(
                        $"PR creation for '{change.RepositoryName}' has an unknown outcome and no open PR was found; reconcile before retrying.");
                }

                await host.UpdateDescriptionAsync(found.Id,
                    PullRequestDescriptionBuilder.Build(evidence, revision, []), ct);
                await tracking.UpsertAsync(new PullRequestTracking(
                    task.Id, change.RepositoryId, found.Id, found.Url,
                    diff.HeadCommit, true, clock.GetUtcNow()), ct);
                created.Add(found);
                continue;
            }

            await tracking.UpsertAsync(new PullRequestTracking(
                task.Id, change.RepositoryId, createdRef.Id, createdRef.Url,
                diff.HeadCommit, true, clock.GetUtcNow()), ct);
            created.Add(createdRef);

            await audit.RecordAsync(new AuditEntry(
                Actor: "orchestrator",
                TaskId: task.Id,
                Action: "pullrequest.created",
                Reason: $"Pull request {createdRef.Id} created for '{change.RepositoryName}'.",
                Outcome: "Success",
                CorrelationId: context.StepId.ToString(),
                Details: new Dictionary<string, string>
                {
                    ["pullRequestId"] = createdRef.Id.ToString(),
                    ["repository"] = change.RepositoryName,
                }), ct);
        }

        if (created.Count == 0)
            return Failure("No changes to open pull requests for.");

        foreach (var pr in created)
        {
            var others = created.Where(o => o.Id != pr.Id).ToList();
            if (others.Count == 0)
                continue;

            await host.UpdateDescriptionAsync(pr.Id,
                PullRequestDescriptionBuilder.Build(evidence, revision, others), ct);
        }

        return new StepOutcome(TaskTrigger.PullRequestOpened, null, null);
    }

    private static StepOutcome Failure(string reason) => new(null, null, reason);

    private async Task<PlanRevision?> LatestConfirmedRevisionAsync(Guid taskId, CancellationToken ct)
    {
        var revisions = await confirmations.ListRevisionsAsync(taskId, ct);
        return revisions
            .Where(r => r.ConfirmationStatus == ConfirmationStatus.Confirmed)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<string>> ReviewerIdsAsync(Guid repositoryId, CancellationToken ct)
    {
        var owners = await ownership.FindOwnersAsync(
            Domain.Identity.OwnershipSubject.Repository, repositoryId.ToString(), ct);
        return owners
            .Where(o => !string.IsNullOrWhiteSpace(o.AzureDevOpsId))
            .Select(o => o.AzureDevOpsId!)
            .ToList();
    }

    /// <summary>Parses <c>https://dev.azure.com/{org}/{project}/_git/{repo}</c>.</summary>
    internal static bool TryParseRemote(
        string remoteUrl, out string organization, out string project, out string repositoryName)
    {
        organization = project = repositoryName = string.Empty;
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 4
            || !segments[2].Equals("_git", StringComparison.OrdinalIgnoreCase))
            return false;

        organization = uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            ? segments[0]
            : uri.Host;
        project = segments[1];
        repositoryName = segments[3].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[3][..^4]
            : segments[3];
        return true;
    }
}
