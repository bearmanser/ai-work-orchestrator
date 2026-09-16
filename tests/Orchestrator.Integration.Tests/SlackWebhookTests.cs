using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class SlackWebhookTests(PostgresFixture fixture, OrchestratorApiFactory factory)
    : IClassFixture<OrchestratorApiFactory>
{
    private const string SigningSecret = "test-slack-signing-secret-12345";
    private const string SecretVariableName = "ORCHESTRATOR_SECRET_SLACK_SIGNING_SECRET";
    private const string BotUserVariableName = "Orchestrator__Slack__BotUserId";
    private const string OrchestratorBotUserId = "U-orchestrator";

    [Fact]
    public async Task Post_UrlVerification_EchoesTheChallenge()
    {
        await ResetAsync();

        var response = await SignedPostAsync("/webhooks/slack",
            """{"type":"url_verification","challenge":"abc123"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge").GetString()
            .ShouldBe("abc123");
    }

    [Fact]
    public async Task Post_UnsignedRequest_Returns401AndEnqueuesNothing()
    {
        await ResetAsync();

        var response = await UnsignedPostAsync("/webhooks/slack", AppMentionPayload("evt-1"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Post_SameEventIdTwice_EnqueuesOnce()
    {
        await ResetAsync();
        var payload = AppMentionPayload("evt-dup");

        var first = await SignedPostAsync("/webhooks/slack", payload);
        var second = await SignedPostAsync("/webhooks/slack", payload);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync(m => m.MessageType == "slack.event")).ShouldBe(1);
    }

    [Fact]
    public async Task Post_SlackRetryHeader_IsStillDeduplicatedByEventId()
    {
        await ResetAsync();
        var payload = AppMentionPayload("evt-retry");

        await SignedPostAsync("/webhooks/slack", payload);
        var retry = await SignedPostAsync("/webhooks/slack", payload, retryNum: 1, retryReason: "http_timeout");

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync(m => m.MessageType == "slack.event")).ShouldBe(1);
    }

    [Fact]
    public async Task Post_MessageFromABot_IsIgnored()
    {
        await ResetAsync();

        var response = await SignedPostAsync("/webhooks/slack", BotMessagePayload("evt-bot"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync(m => m.MessageType == "slack.event")).ShouldBe(0);
    }

    [Fact]
    public async Task Post_MessageFromTheOrchestratorItself_IsIgnored()
    {
        await ResetAsync();

        // Otherwise every status update the orchestrator posts starts a new task.
        var response = await SignedPostAsync("/webhooks/slack", MessagePayload("evt-self", userId: OrchestratorBotUserId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = fixture.NewDbContext();
        (await db.Outbox.CountAsync(m => m.MessageType == "slack.event")).ShouldBe(0);
    }

    [Fact]
    public async Task Post_ReturnsWithinThreeSeconds()
    {
        await ResetAsync();

        var stopwatch = Stopwatch.StartNew();
        var response = await SignedPostAsync("/webhooks/slack", AppMentionPayload("evt-fast"));
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task ResetAsync()
    {
        // EnvironmentSecretStore maps SecretRef("slack.signing-secret")
        // to this variable; the API host runs in-process so it sees the same value.
        Environment.SetEnvironmentVariable(SecretVariableName, SigningSecret);
        // The endpoint reads the orchestrator bot user id from
        // configuration "Orchestrator:Slack:BotUserId", which the in-process
        // host binds from this double-underscore environment variable.
        Environment.SetEnvironmentVariable(BotUserVariableName, OrchestratorBotUserId);
        await fixture.ResetAsync();
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
    }

    private async Task<HttpResponseMessage> SignedPostAsync(
        string path, string body, int? retryNum = null, string? retryReason = null)
    {
        using var client = factory.CreateClient();
        // Sign with the API host's clock (a fixed ManualTimeProvider), not the
        // wall clock, so the endpoint's five-minute skew window always passes.
        var timestamp = factory.Clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        request.Headers.Add("X-Slack-Signature", ComputeSignature(SigningSecret, timestamp, body));
        if (retryNum.HasValue)
            request.Headers.Add("X-Slack-Retry-Num", retryNum.Value.ToString());
        if (retryReason is not null)
            request.Headers.Add("X-Slack-Retry-Reason", retryReason);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UnsignedPostAsync(string path, string body)
    {
        using var client = factory.CreateClient();
        return await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static string ComputeSignature(string secret, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"));
        return "v0=" + Convert.ToHexStringLower(hash);
    }

    private static string AppMentionPayload(string eventId) => JsonSerializer.Serialize(new
    {
        type = "event_callback",
        event_id = eventId,
        team_id = "T-1",
        @event = new
        {
            type = "app_mention",
            user = "U-magnus",
            channel = "C-123",
            ts = "1758000000.0001",
            text = "<@U-orchestrator> please do the work",
        },
    });

    private static string BotMessagePayload(string eventId) => JsonSerializer.Serialize(new
    {
        type = "event_callback",
        event_id = eventId,
        team_id = "T-1",
        @event = new
        {
            type = "message",
            subtype = "bot_message",
            bot_id = "B-123",
            user = "U-bot",
            channel = "C-123",
            ts = "1758000000.0002",
            text = "bot says hi",
        },
    });

    private static string MessagePayload(string eventId, string userId) => JsonSerializer.Serialize(new
    {
        type = "event_callback",
        event_id = eventId,
        team_id = "T-1",
        @event = new
        {
            type = "message",
            user = userId,
            channel = "C-123",
            ts = "1758000000.0003",
            text = "status update from the orchestrator",
        },
    });
}
