using System.Text;
using System.Text.Json;
using Orchestrator.Adapters.Slack;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;

namespace Orchestrator.Api.Webhooks;

/// <summary>
/// Handles Slack interactive payloads (Block Kit button clicks). Authenticates
/// like the events endpoint, parses the action, and delegates to the plan
/// confirmation service. Always acknowledges with 200 so Slack never retries
/// or disables the interaction.
/// </summary>
public static class SlackInteractiveEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/webhooks/slack/interactive", HandleAsync).AllowAnonymous();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        ISecretStore secrets,
        PlanConfirmationService confirmation,
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

        var interaction = ParseInteraction(rawBody);
        if (interaction is null)
            return Results.BadRequest();

        await confirmation.HandleInteractionAsync(interaction, ct);
        return Results.Ok();
    }

    internal static SlackInteraction? ParseInteraction(string rawBody)
    {
        // Slack posts interactive payloads as a form body: payload=<url-encoded JSON>.
        const string prefix = "payload=";
        var encoded = rawBody.StartsWith(prefix, StringComparison.Ordinal)
            ? rawBody[prefix.Length..]
            : rawBody;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(System.Net.WebUtility.UrlDecode(encoded));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("actions", out var actions)
                || actions.ValueKind != JsonValueKind.Array
                || actions.GetArrayLength() == 0)
                return null;

            var action = actions[0];
            if (!action.TryGetProperty("action_id", out var actionId)
                || !action.TryGetProperty("value", out var value)
                || !root.TryGetProperty("user", out var user)
                || !user.TryGetProperty("id", out var userId))
                return null;

            return new SlackInteraction(
                actionId.GetString() ?? string.Empty,
                value.GetString() ?? string.Empty,
                userId.GetString() ?? string.Empty,
                Text: null);
        }
    }
}
