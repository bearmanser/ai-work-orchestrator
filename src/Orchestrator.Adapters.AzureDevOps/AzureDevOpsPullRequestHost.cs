using System.Text.Json;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.AzureDevOps;

/// <summary>
/// <see cref="IPullRequestHost"/> backed by Azure DevOps git APIs (api-version 7.1).
/// Branch names are sent <c>refs/heads/</c>-prefixed; duplicate detection lists
/// open PRs by source ref before creating.
/// </summary>
public sealed class AzureDevOpsPullRequestHost(
    AzureDevOpsHttpClient transport,
    AzureDevOpsOptions options) : IPullRequestHost
{
    private const string ApiVersion = "api-version=7.1";

    public async Task<PullRequestRef> CreateAsync(CreatePullRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Project);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryName);

        var basePath = AzureDevOpsHttpClient.RepoBase(
            request.Organization, request.Project, request.RepositoryName);
        var body = new
        {
            sourceRefName = AzureDevOpsHttpClient.EnsureRefsHeads(request.SourceBranch),
            targetRefName = AzureDevOpsHttpClient.EnsureRefsHeads(request.TargetBranch),
            title = request.Title,
            description = request.Description,
            reviewers = request.RequiredReviewerIds
                .Select(id => new { id, isRequired = true })
                .ToArray(),
        };

        using var document = await transport.SendAsync(
            HttpMethod.Post, $"{basePath}/pullrequests?{ApiVersion}", body, ct).ConfigureAwait(false);
        return ParseRef(document.RootElement);
    }

    public async Task<PullRequestRef?> FindOpenBySourceBranchAsync(
        string organization,
        string project,
        string repositoryName,
        string sourceBranch,
        CancellationToken ct)
    {
        var basePath = AzureDevOpsHttpClient.RepoBase(organization, project, repositoryName);
        var sourceRef = AzureDevOpsHttpClient.EnsureRefsHeads(sourceBranch);
        var url = $"{basePath}/pullrequests" +
            $"?searchCriteria.sourceRefName={Uri.EscapeDataString(sourceRef)}" +
            $"&searchCriteria.status=active&{ApiVersion}";

        using var document = await transport.SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() == 0)
            return null;

        return ParseRef(value[0]);
    }

    public async Task<PullRequestDetail> GetAsync(int pullRequestId, CancellationToken ct)
    {
        var basePath = AzureDevOpsHttpClient.RepoBase(
            options.Organization, options.Project, options.RepositoryName);

        using var pr = await transport.SendAsync(
            HttpMethod.Get, $"{basePath}/pullrequests/{pullRequestId}?{ApiVersion}", null, ct).ConfigureAwait(false);
        using var threads = await transport.SendAsync(
            HttpMethod.Get, $"{basePath}/pullrequests/{pullRequestId}/threads?{ApiVersion}", null, ct).ConfigureAwait(false);
        using var policies = await transport.SendAsync(
            HttpMethod.Get, $"{basePath}/pullrequests/{pullRequestId}/policyevaluations?{ApiVersion}", null, ct).ConfigureAwait(false);

        var reference = ParseRef(pr.RootElement);
        return new PullRequestDetail(
            reference,
            ParseStatus(GetString(pr.RootElement, "status")),
            ParseReviewers(pr.RootElement),
            ParseThreads(threads.RootElement),
            ParsePolicies(policies.RootElement));
    }

    public async Task ReplyToThreadAsync(int pullRequestId, int threadId, string text, CancellationToken ct)
    {
        var basePath = AzureDevOpsHttpClient.RepoBase(
            options.Organization, options.Project, options.RepositoryName);
        using var _ = await transport.SendAsync(
            HttpMethod.Post,
            $"{basePath}/pullrequests/{pullRequestId}/threads/{threadId}/comments?{ApiVersion}",
            new { content = text },
            ct).ConfigureAwait(false);
    }

    public async Task UpdateDescriptionAsync(int pullRequestId, string description, CancellationToken ct)
    {
        var basePath = AzureDevOpsHttpClient.RepoBase(
            options.Organization, options.Project, options.RepositoryName);
        using var _ = await transport.SendAsync(
            new HttpMethod("PATCH"),
            $"{basePath}/pullrequests/{pullRequestId}?{ApiVersion}",
            new { description },
            ct).ConfigureAwait(false);
    }

    private static PullRequestRef ParseRef(JsonElement element) => new(
        GetInt(element, "pullRequestId"),
        GetString(element, "url") ?? string.Empty,
        GetString(element, "sourceRefName") ?? string.Empty,
        GetString(element, "targetRefName") ?? string.Empty,
        element.TryGetProperty("lastMergeSourceCommit", out var commit)
            && commit.ValueKind == JsonValueKind.Object
            ? GetString(commit, "commitId") ?? string.Empty
            : string.Empty);

    private static PullRequestStatus ParseStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "active" => PullRequestStatus.Active,
            "completed" => PullRequestStatus.Completed,
            "abandoned" => PullRequestStatus.Abandoned,
            _ => PullRequestStatus.Unknown,
        };

    private static IReadOnlyList<ReviewerVote> ParseReviewers(JsonElement pr)
    {
        if (!pr.TryGetProperty("reviewers", out var reviewers)
            || reviewers.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<ReviewerVote>();
        foreach (var reviewer in reviewers.EnumerateArray())
        {
            list.Add(new ReviewerVote(
                GetString(reviewer, "id") ?? string.Empty,
                GetString(reviewer, "displayName") ?? string.Empty,
                GetInt(reviewer, "vote"),
                GetBool(reviewer, "isRequired")));
        }

        return list;
    }

    private static IReadOnlyList<CommentThread> ParseThreads(JsonElement threads)
    {
        if (!threads.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<CommentThread>();
        foreach (var thread in value.EnumerateArray())
        {
            var comments = new List<Comment>();
            if (thread.TryGetProperty("comments", out var commentArray)
                && commentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var comment in commentArray.EnumerateArray())
                {
                    comments.Add(new Comment(
                        GetString(comment, "content") ?? string.Empty,
                        GetString(comment, "author") ?? GetString(comment, "authorId")));
                }
            }

            string? filePath = GetString(thread, "filePath");
            if (filePath is null
                && thread.TryGetProperty("threadContext", out var context)
                && context.ValueKind == JsonValueKind.Object)
                filePath = GetString(context, "filePath");

            list.Add(new CommentThread(
                GetInt(thread, "id"),
                GetString(thread, "status") ?? string.Empty,
                comments,
                filePath));
        }

        return list;
    }

    private static IReadOnlyList<PolicyEvaluation> ParsePolicies(JsonElement policies)
    {
        if (!policies.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<PolicyEvaluation>();
        foreach (var policy in value.EnumerateArray())
        {
            var id = GetString(policy, "evaluationId")
                ?? GetString(policy, "id")
                ?? string.Empty;
            var status = GetString(policy, "status") ?? string.Empty;
            string? displayName = GetString(policy, "displayName");
            if (displayName is null
                && policy.TryGetProperty("configuration", out var configuration)
                && configuration.ValueKind == JsonValueKind.Object
                && configuration.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.Object)
                displayName = GetString(type, "displayName");

            list.Add(new PolicyEvaluation(id, status, displayName));
        }

        return list;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : false;
}
