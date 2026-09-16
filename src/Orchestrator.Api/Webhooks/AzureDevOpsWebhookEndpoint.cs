using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orchestrator.Adapters.AzureDevOps;
using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Api.Webhooks;

/// <summary>
/// Azure DevOps service hook intake. Authenticates by HTTP Basic against the
/// <c>azuredevops.webhook-password</c> secret, deduplicates redeliveries by the
/// payload's <c>id</c> field, and enqueues a <c>pullrequest.event</c> outbox message
/// for pull request events. No PR processing happens in the request: the endpoint
/// returns 200 immediately (including for duplicates and unknown event types) so
/// Azure DevOps never disables the subscription for slow or failing deliveries.
/// Malformed JSON is a 400; bad credentials are a 401.
/// </summary>
public static class AzureDevOpsWebhookEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/webhooks/azure-devops", HandleAsync).AllowAnonymous();
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        ISecretStore secrets,
        IEventRegistry registry,
        IOutboxWriter outbox,
        OrchestratorDbContext db,
        CancellationToken ct)
    {
        string expectedPassword;
        try
        {
            expectedPassword = await secrets.GetAsync(
                new SecretRef("azuredevops.webhook-password"), ct);
        }
        catch (SecretNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (!BasicPasswordValid(http, expectedPassword))
            return Results.Unauthorized();

        string rawBody;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync(ct);

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
            var parsed = ServiceHookParser.Parse(document);
            if (parsed is null)
                return Results.Ok();

            var payloadHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawBody)));
            var registration = await registry.TryRegisterAsync(
                "azuredevops", parsed.EventId, payloadHash, ct);
            if (!registration.IsFirstDelivery)
                return Results.Ok();

            outbox.Enqueue(
                "pullrequest.event", parsed, taskId: null,
                idempotencyKey: $"azuredevops:{parsed.EventId}");
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }
    }

    private static bool BasicPasswordValid(HttpContext http, string expectedPassword)
    {
        if (!http.Request.Headers.TryGetValue("Authorization", out var values))
            return false;

        const string prefix = "Basic ";
        var header = values.ToString();
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(header[prefix.Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(decoded[(separator + 1)..]),
            Encoding.UTF8.GetBytes(expectedPassword));
    }
}
