using System.Collections.Concurrent;
using System.Text.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// A stateful WireMock stand-in for the Azure DevOps git pull-request REST API,
/// scoped to one fixed organization/project/repository
/// (<c>kameleon/Platform/backend</c> - see AcceptanceHost's remarks on why the
/// repository's git remote is shaped like a real Azure DevOps URL). Tracks
/// created pull requests by source branch in memory, exactly as the real
/// service would persist them, so <c>FindOpenBySourceBranchAsync</c> and
/// <c>GetAsync</c> see real, consistent state across calls - including across
/// an <see cref="AcceptanceHost.RestartAsync"/>, since this server is never
/// torn down, only the API host is.
/// </summary>
public sealed class FakeAzureDevOps
{
    private readonly string BasePath;

    private readonly ConcurrentDictionary<string, PullRequestRecord> _bySourceBranch = new();
    private readonly ConcurrentDictionary<int, List<CommentThreadRecord>> _threads = new();
    private int _nextId = 5000;

    public WireMockServer Server { get; }

    public string BaseUrl => Server.Url!;

    /// <summary>Number of pull-request create calls actually sent to the fake API.</summary>
    public int CreateCalls => Server.LogEntries.Count(e =>
        e.RequestMessage.Path == $"{BasePath}/pullrequests" && e.RequestMessage.Method == "POST");

    /// <param name="repositoryName">
    /// Unique per <see cref="AcceptanceHost"/> instance: every acceptance test
    /// class shares one Postgres database (see AcceptanceCollection), and
    /// <c>RepositoryProfile.RemoteUrl</c> (built from this same name) has a
    /// unique index, so two classes cannot both use a literal "backend".
    /// </param>
    public FakeAzureDevOps(string repositoryName)
    {
        BasePath = $"/kameleon/Platform/_apis/git/repositories/{repositoryName}";
        Server = WireMockServer.Start();

        Server.Given(Request.Create().WithPath($"{BasePath}/pullrequests").UsingPost())
            .RespondWith(Response.Create().WithCallback(HandleCreate));

        Server.Given(Request.Create().WithPath($"{BasePath}/pullrequests").UsingGet())
            .RespondWith(Response.Create().WithCallback(HandleSearch));

        Server.Given(Request.Create().WithPath(new WireMock.Matchers.RegexMatcher(
                $"^{BasePath}/pullrequests/\\d+$")).UsingGet())
            .RespondWith(Response.Create().WithCallback(HandleGet));

        Server.Given(Request.Create().WithPath(new WireMock.Matchers.RegexMatcher(
                $"^{BasePath}/pullrequests/\\d+/threads$")).UsingGet())
            .RespondWith(Response.Create().WithCallback(HandleThreads));

        Server.Given(Request.Create().WithPath(new WireMock.Matchers.RegexMatcher(
                $"^{BasePath}/pullrequests/\\d+/policyevaluations$")).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { value = Array.Empty<object>() }));

        Server.Given(Request.Create().WithPath(new WireMock.Matchers.RegexMatcher(
                $"^{BasePath}/pullrequests/\\d+$")).UsingPatch())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { }));

        Server.Given(Request.Create().WithPath(new WireMock.Matchers.RegexMatcher(
                $"^{BasePath}/pullrequests/\\d+/threads/\\d+/comments$")).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { }));
    }

    public void Stop()
    {
        Server.Stop();
        Server.Dispose();
    }

    /// <summary>Seeds review-thread comments an already-created PR should report back.</summary>
    public void SeedThread(int pullRequestId, int threadId, params (string Author, string Text)[] comments)
    {
        var list = _threads.GetOrAdd(pullRequestId, _ => []);
        lock (list)
        {
            list.RemoveAll(t => t.ThreadId == threadId);
            list.Add(new CommentThreadRecord(threadId, comments.Select(c => (c.Author, c.Text)).ToList()));
        }
    }

    public int? PullRequestIdForBranch(string sourceBranch) =>
        _bySourceBranch.TryGetValue(sourceBranch, out var pr) ? pr.Id : null;

    private WireMock.ResponseMessage HandleCreate(WireMock.IRequestMessage request)
    {
        using var body = JsonDocument.Parse(request.Body ?? "{}");
        var sourceRef = body.RootElement.GetProperty("sourceRefName").GetString()!;
        var targetRef = body.RootElement.GetProperty("targetRefName").GetString()!;

        var record = _bySourceBranch.GetOrAdd(sourceRef, _ => new PullRequestRecord(
            Interlocked.Increment(ref _nextId), sourceRef, targetRef, "commit-" + Guid.NewGuid().ToString("N")[..8]));

        return JsonResponse(201, ToPullRequestJson(record));
    }

    private WireMock.ResponseMessage HandleSearch(WireMock.IRequestMessage request)
    {
        var query = request.GetParameter("searchCriteria.sourceRefName");
        var sourceRef = query is { Count: > 0 } ? query[0] : null;

        if (sourceRef is not null && _bySourceBranch.TryGetValue(sourceRef, out var record))
            return JsonResponse(200, new { value = new[] { ToPullRequestJsonObject(record) } });

        return JsonResponse(200, new { value = Array.Empty<object>() });
    }

    private WireMock.ResponseMessage HandleGet(WireMock.IRequestMessage request)
    {
        var id = int.Parse(request.Path.Split('/')[^1]);
        var record = _bySourceBranch.Values.FirstOrDefault(r => r.Id == id);
        if (record is null)
            return JsonResponse(404, new { message = "not found" });

        var json = ToPullRequestJsonObject(record) as IDictionary<string, object?>;
        return JsonResponse(200, new
        {
            pullRequestId = record.Id,
            url = $"https://dev.azure.com/kameleon/_apis/git/pullRequests/{record.Id}",
            sourceRefName = record.SourceBranch,
            targetRefName = record.TargetBranch,
            lastMergeSourceCommit = new { commitId = record.HeadCommit },
            status = "active",
            reviewers = Array.Empty<object>(),
        });
    }

    private WireMock.ResponseMessage HandleThreads(WireMock.IRequestMessage request)
    {
        var id = int.Parse(request.Path.Split('/')[^2]);
        var threads = _threads.TryGetValue(id, out var list) ? list : [];
        return JsonResponse(200, new
        {
            value = threads.Select(t => new
            {
                id = t.ThreadId,
                status = "active",
                comments = t.Comments.Select(c => new { content = c.Text, author = c.Author }),
            }),
        });
    }

    private static object ToPullRequestJson(PullRequestRecord record) => ToPullRequestJsonObject(record);

    private static object ToPullRequestJsonObject(PullRequestRecord record) => new
    {
        pullRequestId = record.Id,
        url = $"https://dev.azure.com/kameleon/_apis/git/pullRequests/{record.Id}",
        sourceRefName = record.SourceBranch,
        targetRefName = record.TargetBranch,
        lastMergeSourceCommit = new { commitId = record.HeadCommit },
    };

    private static WireMock.ResponseMessage JsonResponse(int statusCode, object body) => new()
    {
        StatusCode = statusCode,
        BodyData = new WireMock.Util.BodyData
        {
            BodyAsJson = body,
            DetectedBodyType = WireMock.Types.BodyType.Json,
        },
    };

    private sealed record PullRequestRecord(int Id, string SourceBranch, string TargetBranch, string HeadCommit);

    private sealed record CommentThreadRecord(int ThreadId, List<(string Author, string Text)> Comments);
}
