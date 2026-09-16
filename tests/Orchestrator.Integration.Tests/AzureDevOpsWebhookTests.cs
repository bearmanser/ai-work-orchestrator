using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class AzureDevOpsWebhookTests(PostgresFixture fixture, OrchestratorApiFactory factory)
    : IClassFixture<OrchestratorApiFactory>
{
    private const string WebhookPassword = "test-azuredevops-webhook-password";
    private const string SecretVariableName = "ORCHESTRATOR_SECRET_AZUREDEVOPS_WEBHOOK_PASSWORD";

    private HttpClient WebhookClient => CreateClient(WebhookPassword);

    private HttpClient UnauthenticatedWebhookClient => CreateClient("wrong-password");

    [Fact]
    public async Task Post_ValidBasicAuthAndKnownEvent_Returns200AndEnqueuesOnce()
    {
        await ResetAsync();
        using var client = WebhookClient;

        var response = await client.PostAsync("/webhooks/azure-devops",
            JsonContent(PullRequestUpdatedPayload(eventId: "evt-1", pullRequestId: 4711)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync(m => m.MessageType == "pullrequest.event")).ShouldBe(1);
    }

    [Fact]
    public async Task Post_SameEventIdTwice_EnqueuesOnlyOnce()
    {
        await ResetAsync();
        using var client = WebhookClient;

        var payload = PullRequestUpdatedPayload(eventId: "evt-dup", pullRequestId: 4711);
        await client.PostAsync("/webhooks/azure-devops", JsonContent(payload));
        var second = await client.PostAsync("/webhooks/azure-devops", JsonContent(payload));

        second.StatusCode.ShouldBe(HttpStatusCode.OK); // still 200 so the hook is not disabled
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync(m => m.MessageType == "pullrequest.event")).ShouldBe(1);
    }

    [Fact]
    public async Task Post_WrongPassword_Returns401AndEnqueuesNothing()
    {
        await ResetAsync();
        using var client = UnauthenticatedWebhookClient;

        var response = await client.PostAsync("/webhooks/azure-devops",
            JsonContent(PullRequestUpdatedPayload("evt-2", 4711)));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Post_UnknownEventType_Returns200AndEnqueuesNothing()
    {
        await ResetAsync();
        using var client = WebhookClient;

        var response = await client.PostAsync("/webhooks/azure-devops",
            JsonContent(new { id = "evt-3", eventType = "build.complete", resource = new { } }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Post_CommentEvent_ExtractsPullRequestIdFromNestedResource()
    {
        await ResetAsync();
        using var client = WebhookClient;

        await client.PostAsync("/webhooks/azure-devops", JsonContent(CommentEventPayload("evt-4", 4711)));

        await using var db = fixture.NewDbContext();
        var message = await db.Outbox.SingleAsync(m => m.MessageType == "pullrequest.event");
        message.PayloadJson.ShouldContain("4711");
    }

    [Fact]
    public async Task Post_MalformedJson_Returns400WithoutThrowing()
    {
        await ResetAsync();
        using var client = WebhookClient;

        var response = await client.PostAsync("/webhooks/azure-devops",
            new StringContent("{not json", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task ResetAsync()
    {
        // EnvironmentSecretStore maps SecretRef("azuredevops.webhook-password")
        // to this variable; the API host runs in-process so it sees the same value.
        Environment.SetEnvironmentVariable(SecretVariableName, WebhookPassword);
        await fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
    }

    private HttpClient CreateClient(string password)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"webhook:{password}")));
        return client;
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static object PullRequestUpdatedPayload(string eventId, int pullRequestId) => new
    {
        id = eventId,
        eventType = "git.pullrequest.updated",
        resource = new
        {
            pullRequestId,
            repository = new { name = "my-repo" },
            lastMergeSourceCommit = new { commitId = "abc123" },
            createdBy = new { id = "user-1" },
        },
    };

    private static object CommentEventPayload(string eventId, int pullRequestId) => new
    {
        id = eventId,
        eventType = "ms.vss-code.git-pullrequest-comment-event",
        resource = new
        {
            pullRequest = new
            {
                pullRequestId,
                repository = new { name = "my-repo" },
            },
            comment = new
            {
                author = new { id = "user-2" },
            },
        },
    };
}
