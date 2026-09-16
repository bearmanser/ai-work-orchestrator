using System.Text.Json;

namespace Orchestrator.Adapters.AzureDevOps;

/// <summary>
/// A pull request event extracted from an Azure DevOps service hook payload.
/// </summary>
public sealed record PullRequestEvent(
    string EventId,
    string EventType,
    int PullRequestId,
    string RepositoryName,
    string? LastMergeSourceCommit,
    string? ActorId);

/// <summary>
/// Parses Azure DevOps service hook payloads. Handles <c>git.pullrequest.updated</c>
/// and <c>git.pullrequest.merged</c> (PR id at <c>resource.pullRequestId</c>) as well as
/// <c>ms.vss-code.git-pullrequest-comment-event</c> (PR id nested at
/// <c>resource.pullRequest.pullRequestId</c>). Returns <c>null</c> for anything else
/// so the caller can acknowledge the delivery without enqueuing work.
/// </summary>
public static class ServiceHookParser
{
    private static readonly HashSet<string> KnownEventTypes = new(StringComparer.Ordinal)
    {
        "git.pullrequest.updated",
        "git.pullrequest.merged",
        "ms.vss-code.git-pullrequest-comment-event",
    };

    private const string CommentEventType = "ms.vss-code.git-pullrequest-comment-event";

    public static PullRequestEvent? Parse(JsonDocument payload)
    {
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetString(root, "eventType", out var eventType)
            || !KnownEventTypes.Contains(eventType))
            return null;

        if (!TryGetString(root, "id", out var eventId)
            || string.IsNullOrWhiteSpace(eventId))
            return null;

        if (!root.TryGetProperty("resource", out var resource)
            || resource.ValueKind != JsonValueKind.Object)
            return null;

        return eventType == CommentEventType
            ? ParseCommentEvent(eventId, eventType, resource)
            : ParsePullRequestEvent(eventId, eventType, resource);
    }

    private static PullRequestEvent? ParsePullRequestEvent(
        string eventId, string eventType, JsonElement resource)
    {
        if (!TryGetInt(resource, "pullRequestId", out var pullRequestId))
            return null;

        return new PullRequestEvent(
            eventId,
            eventType,
            pullRequestId,
            RepositoryName(resource),
            LastMergeSourceCommit(resource),
            ActorId(resource));
    }

    private static PullRequestEvent? ParseCommentEvent(
        string eventId, string eventType, JsonElement resource)
    {
        if (!resource.TryGetProperty("pullRequest", out var pullRequest)
            || pullRequest.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetInt(pullRequest, "pullRequestId", out var pullRequestId))
            return null;

        string? actor = null;
        if (resource.TryGetProperty("comment", out var comment)
            && comment.ValueKind == JsonValueKind.Object)
            actor = ActorId(comment);

        return new PullRequestEvent(
            eventId,
            eventType,
            pullRequestId,
            RepositoryName(pullRequest),
            LastMergeSourceCommit(pullRequest),
            actor ?? ActorId(resource));
    }

    private static string RepositoryName(JsonElement element)
    {
        if (element.TryGetProperty("repository", out var repository)
            && repository.ValueKind == JsonValueKind.Object
            && TryGetString(repository, "name", out var name))
            return name;

        return string.Empty;
    }

    private static string? LastMergeSourceCommit(JsonElement element)
    {
        if (element.TryGetProperty("lastMergeSourceCommit", out var commit)
            && commit.ValueKind == JsonValueKind.Object
            && TryGetString(commit, "commitId", out var commitId))
            return commitId;

        return null;
    }

    private static string? ActorId(JsonElement element)
    {
        foreach (var property in new[] { "createdBy", "author" })
        {
            if (element.TryGetProperty(property, out var actor)
                && actor.ValueKind == JsonValueKind.Object
                && TryGetString(actor, "id", out var id))
                return id;
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;

        var text = property.GetString();
        if (text is null)
            return false;

        value = text;
        return true;
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            return true;

        // Service hook payloads occasionally carry the id as a string.
        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out value))
            return true;

        return false;
    }
}
