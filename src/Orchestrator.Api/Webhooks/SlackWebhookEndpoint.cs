using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orchestrator.Adapters.Slack;
using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Api.Webhooks;

/// <summary>
/// Slack event intake. Authenticates every request with
/// <see cref="SlackSignatureVerifier"/> against the <c>slack.signing-secret</c>
/// secret, answers <c>url_verification</c> with the challenge echo, deduplicates
/// redeliveries by Slack's <c>event_id</c>, and enqueues a <c>slack.event</c>
/// outbox message. No task work happens in the request: the endpoint returns 200
/// immediately (including for duplicates, bot messages, and unknown event types)
/// so Slack never disables the subscription for slow deliveries. Messages from
/// bots or from the orchestrator itself are acknowledged with 200 and dropped so
/// status updates never start new tasks. Malformed JSON is a 400; bad signatures
/// are a 401.
/// </summary>
public static class SlackWebhookEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/webhooks/slack", HandleAsync).AllowAnonymous();
        app.MapPost("/webhooks/slack/interactive", HandleInteractiveAsync).AllowAnonymous();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        ISecretStore secrets,
        IEventRegistry registry,
        IOutboxWriter outbox,
        OrchestratorDbContext db,
        IConfiguration configuration,
        TimeProvider clock,
        CancellationToken ct)
    {
        string signingSecret;
        try
        {
            signingSecret = await secrets.GetAsync(
                new SecretRef("slack.signing-secret"), ct);
        }
        catch (SecretNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        http.Request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync(ct);

        if (!SlackSignatureVerifier.Verify(
                signingSecret,
                http.Request.Headers["X-Slack-Request-Timestamp"].ToString(),
                http.Request.Headers["X-Slack-Signature"].ToString(),
                rawBody,
                clock.GetUtcNow()))
            return Results.Unauthorized();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Results.BadRequest();

            if (IsUrlVerification(root, out var challenge))
                return Results.Json(new { challenge });

            if (!root.TryGetProperty("event", out var @event)
                || @event.ValueKind != JsonValueKind.Object)
                return Results.Ok();

            if (IsBotOrSelf(@event, configuration))
                return Results.Ok();

            var parsed = SlackEventParser.Parse(document);
            if (parsed is null)
                return Results.Ok();

            var payloadHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawBody)));
            var registration = await registry.TryRegisterAsync(
                "slack", parsed.EventId, payloadHash, ct);
            if (!registration.IsFirstDelivery)
                return Results.Ok();

            outbox.Enqueue(
                "slack.event", parsed, taskId: null,
                idempotencyKey: $"slack:{parsed.EventId}");
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }
    }

    private static async Task<IResult> HandleInteractiveAsync(
        HttpContext http,
        ISecretStore secrets,
        TimeProvider clock,
        CancellationToken ct)
    {
        string signingSecret;
        try
        {
            signingSecret = await secrets.GetAsync(
                new SecretRef("slack.signing-secret"), ct);
        }
        catch (SecretNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        http.Request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync(ct);

        if (!SlackSignatureVerifier.Verify(
                signingSecret,
                http.Request.Headers["X-Slack-Request-Timestamp"].ToString(),
                http.Request.Headers["X-Slack-Signature"].ToString(),
                rawBody,
                clock.GetUtcNow()))
            return Results.Unauthorized();

        return Results.Ok();
    }

    private static bool IsUrlVerification(JsonElement root, out string? challenge)
    {
        challenge = null;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() != "url_verification")
            return false;

        if (!root.TryGetProperty("challenge", out var challengeProperty)
            || challengeProperty.ValueKind != JsonValueKind.String)
            return false;

        challenge = challengeProperty.GetString();
        return true;
    }

    private static bool IsBotOrSelf(JsonElement @event, IConfiguration configuration)
    {
        if (@event.TryGetProperty("bot_id", out var botId)
            && botId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(botId.GetString()))
            return true;

        if (@event.TryGetProperty("subtype", out var subtype)
            && subtype.ValueKind == JsonValueKind.String
            && subtype.GetString() == "bot_message")
            return true;

        var botUserId = configuration["Orchestrator:Slack:BotUserId"];
        if (!string.IsNullOrWhiteSpace(botUserId)
            && @event.TryGetProperty("user", out var user)
            && user.ValueKind == JsonValueKind.String
            && user.GetString() == botUserId)
            return true;

        return false;
    }
}
