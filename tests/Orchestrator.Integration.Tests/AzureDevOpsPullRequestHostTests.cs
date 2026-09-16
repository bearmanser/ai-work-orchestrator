using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Adapters.AzureDevOps;
using Orchestrator.Application.Ports;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Orchestrator.Integration.Tests;

[Trait("Category", "Integration")]
public sealed class AzureDevOpsPullRequestHostTests : IAsyncLifetime
{
    private WireMockServer _server = null!;
    private IPullRequestHost Host = null!;

    private const string TestPat = "test-pat-super-secret-value-12345";

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        Host = BuildHost();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_PostsToCorrectRouteWithBasicAuth()
    {
        _server.Given(Request.Create()
                .WithPath("/kameleon/Platform/_apis/git/repositories/backend/pullrequests")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                pullRequestId = 4711,
                url = "https://dev.azure.com/kameleon/_apis/git/pullRequests/4711",
                sourceRefName = "refs/heads/orchestrator/task-1",
                targetRefName = "refs/heads/main",
                lastMergeSourceCommit = new { commitId = "abc123" },
            }));

        var pr = await Host.CreateAsync(NewRequest("orchestrator/task-1", "main"), default);

        pr.Id.ShouldBe(4711);
        var received = _server.LogEntries.Single();
        received.RequestMessage.Headers!["Authorization"].Single().ShouldStartWith("Basic ");
    }

    [Fact]
    public async Task Create_SendsRefsHeadsPrefixedBranchNames()
    {
        // Azure DevOps rejects bare branch names.
        SetupCreated();
        await Host.CreateAsync(NewRequest("orchestrator/task-1", "main"), default);

        var body = JsonDocument.Parse(_server.LogEntries.Single().RequestMessage.Body!);
        body.RootElement.GetProperty("sourceRefName").GetString().ShouldBe("refs/heads/orchestrator/task-1");
        body.RootElement.GetProperty("targetRefName").GetString().ShouldBe("refs/heads/main");
    }

    [Fact]
    public async Task FindOpenBySourceBranch_ExistingPr_ReturnsIt()
    {
        SetupSearchReturning(pullRequestId: 4711, sourceRef: "refs/heads/orchestrator/task-1");

        var found = await Host.FindOpenBySourceBranchAsync("kameleon", "Platform", "backend",
            "orchestrator/task-1", default);

        found!.Id.ShouldBe(4711);
    }

    [Fact]
    public async Task FindOpenBySourceBranch_NoMatch_ReturnsNull()
    {
        SetupSearchReturning();
        (await Host.FindOpenBySourceBranchAsync("kameleon", "Platform", "backend",
            "orchestrator/task-1", default)).ShouldBeNull();
    }

    [Fact]
    public async Task Get_ParsesReviewerVotesAndThreadStatuses()
    {
        SetupDetail(reviewers: new[] { new { id = "U-reviewer", displayName = "Reviewer", vote = 10, isRequired = true } },
                    threads: new[] { new { id = 7, status = "active", comments = new[] { new { content = "please rename" } } } });

        var detail = await Host.GetAsync(4711, default);

        detail.Reviewers.Single().Vote.ShouldBe(10);
        detail.Threads.Single().Status.ShouldBe("active");
    }

    [Fact]
    public async Task Create_ServerReturns500_ThrowsAfterRetries()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(500));
        await Should.ThrowAsync<HttpRequestException>(() => Host.CreateAsync(NewRequest("b", "main"), default));
        _server.LogEntries.Count().ShouldBeGreaterThan(1); // resilience pipeline retried
    }

    [Fact]
    public async Task Create_ExceptionMessage_DoesNotContainThePat()
    {
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(401));
        var ex = await Should.ThrowAsync<HttpRequestException>(() => Host.CreateAsync(NewRequest("b", "main"), default));
        ex.ToString().ShouldNotContain(TestPat);
    }

    private static CreatePullRequest NewRequest(string sourceBranch, string targetBranch) =>
        new(
            Organization: "kameleon",
            Project: "Platform",
            RepositoryName: "backend",
            SourceBranch: sourceBranch,
            TargetBranch: targetBranch,
            Title: "Test PR",
            Description: "Test description",
            RequiredReviewerIds: []);

    private IPullRequestHost BuildHost()
    {
        var secrets = new FakeSecretStore(TestPat);
        var options = new AzureDevOpsOptions
        {
            BaseUrl = _server.Url!,
            Organization = "kameleon",
            Project = "Platform",
            RepositoryName = "backend",
            PatSecret = new SecretRef("azuredevops.pat"),
        };

        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(secrets);
        services.AddSingleton(options);
        services.AddHttpClient<AzureDevOpsHttpClient>(client =>
        {
            client.BaseAddress = new Uri(_server.Url!);
        }).AddStandardResilienceHandler();
        services.AddTransient<IPullRequestHost, AzureDevOpsPullRequestHost>();

        return services.BuildServiceProvider().GetRequiredService<IPullRequestHost>();
    }

    private void SetupCreated() =>
        _server.Given(Request.Create()
                .WithPath("/kameleon/Platform/_apis/git/repositories/backend/pullrequests")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(new
            {
                pullRequestId = 4711,
                url = "https://dev.azure.com/kameleon/_apis/git/pullRequests/4711",
                sourceRefName = "refs/heads/orchestrator/task-1",
                targetRefName = "refs/heads/main",
                lastMergeSourceCommit = new { commitId = "abc123" },
            }));

    private void SetupSearchReturning(int? pullRequestId = null, string? sourceRef = null)
    {
        object body = pullRequestId is null
            ? new { value = Array.Empty<object>() }
            : new
            {
                value = new[]
                {
                    new
                    {
                        pullRequestId = pullRequestId.Value,
                        url = $"https://dev.azure.com/kameleon/_apis/git/pullRequests/{pullRequestId.Value}",
                        sourceRefName = sourceRef ?? "refs/heads/orchestrator/task-1",
                        targetRefName = "refs/heads/main",
                        lastMergeSourceCommit = new { commitId = "abc123" },
                    },
                },
            };

        _server.Given(Request.Create()
                .WithPath("/kameleon/Platform/_apis/git/repositories/backend/pullrequests")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(body));
    }

    private void SetupDetail(object reviewers, object threads)
    {
        _server.Given(Request.Create()
                .WithPath("/kameleon/Platform/_apis/git/repositories/backend/pullrequests/4711")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                pullRequestId = 4711,
                url = "https://dev.azure.com/kameleon/_apis/git/pullRequests/4711",
                sourceRefName = "refs/heads/orchestrator/task-1",
                targetRefName = "refs/heads/main",
                lastMergeSourceCommit = new { commitId = "abc123" },
                status = "active",
                reviewers,
            }));

        _server.Given(Request.Create()
                .WithPath("/kameleon/Platform/_apis/git/repositories/backend/pullrequests/4711/threads")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = threads,
            }));

        _server.Given(Request.Create()
                .WithPath("/kameleon/Platform/_apis/git/repositories/backend/pullrequests/4711/policyevaluations")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                value = Array.Empty<object>(),
            }));
    }

    private sealed class FakeSecretStore(string pat) : ISecretStore
    {
        public Task<string> GetAsync(SecretRef reference, CancellationToken ct) =>
            Task.FromResult(pat);

        public Task<SecretDescription> DescribeAsync(SecretRef reference, CancellationToken ct) =>
            Task.FromResult(new SecretDescription(Exists: true, LastRotatedAt: null, Fingerprint: "deadbeef"));
    }
}
