using System.Security.Claims;
using Npgsql;
using Orchestrator.Api.Auth;
using Orchestrator.Api.Configuration;
using Orchestrator.Application.Integrations;
using Orchestrator.Application.Ports;

namespace Orchestrator.Api.Endpoints;

/// <summary>
/// One integration's current status. Never carries the secret value itself - only a
/// fingerprint (the first 8 hex characters of SHA256(value)) and existence/rotation
/// metadata from <see cref="ISecretStore.DescribeAsync"/>.
/// </summary>
public sealed record IntegrationStatus(
    string Name,
    bool Configured,
    string SecretFingerprint,
    DateTimeOffset? LastCheckedAt,
    string Health,
    string? Detail);

/// <summary>PUT /api/admin/integrations/{name} body - a reference to a secret, never a value.</summary>
public sealed record SetIntegrationSecretRequest(string SecretRef);

/// <summary>
/// Admin visibility into and configuration of the system's external integrations.
/// Every handler works only with <see cref="ISecretStore.DescribeAsync"/> output and
/// admin-supplied secret *references*; no handler here ever reads or returns a
/// resolved secret value.
/// </summary>
public static class IntegrationEndpoints
{
    /// <summary>
    /// The fixed catalogue of integrations this deployment knows about, and the
    /// built-in secret reference each resolves by default absent an admin override.
    /// Mirrors the defaults baked into the adapters that actually use these
    /// credentials (ClaudeCliOptions, CodexCliOptions, AzureDevOpsOptions, the Slack
    /// webhook endpoints, and NamedQueryDomainDataSource's per-environment P1 keys).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> KnownIntegrations = new Dictionary<string, string?>
    {
        ["slack"] = "slack.signing-secret",
        ["azuredevops"] = "azuredevops.pat",
        ["anthropic"] = "anthropic.api-key",
        ["openai"] = "openai.api-key",
        ["p1"] = "p1.default.connection",
        // Obsidian's vault store is filesystem-backed (Orchestrator:Vault:Root), not
        // secret-backed - it is listed so admins can see it, but has no secret
        // reference. Its status and live check come from VaultOptions instead.
        ["obsidian"] = null,
    };

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/integrations")
            .RequireAuthorization(AdminOnly.PolicyName);
        group.MapGet("/", List);
        group.MapPost("/{name}/test", Test);
        group.MapPut("/{name}", SetSecretRef);
    }

    private static async Task<IResult> List(
        ISecretStore secrets, IIntegrationSettingsStore settings, VaultOptions vault, CancellationToken ct)
    {
        var statuses = new List<IntegrationStatus>();
        foreach (var (name, defaultSecretRef) in KnownIntegrations)
        {
            statuses.Add(await BuildStatusAsync(name, defaultSecretRef, secrets, settings, vault, ct));
        }

        return Results.Json(statuses);
    }

    private static async Task<IResult> Test(
        string name, ClaimsPrincipal user,
        ISecretStore secrets, IIntegrationSettingsStore settings, VaultOptions vault, IAuditWriter audit,
        TimeProvider clock, CancellationToken ct)
    {
        if (!KnownIntegrations.TryGetValue(name, out var defaultSecretRef))
            return Results.NotFound();

        var (health, detail) = await RunLiveCheckAsync(name, defaultSecretRef, settings, secrets, vault, ct);
        var now = clock.GetUtcNow();
        await settings.RecordTestResultAsync(name, health, detail, now, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: Actor(user),
            TaskId: null,
            Action: "integration.test",
            Reason: $"Connectivity test for integration '{name}' reported {health}.",
            Outcome: health,
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string> { ["name"] = name }), ct);

        var status = await BuildStatusAsync(name, defaultSecretRef, secrets, settings, vault, ct);
        return Results.Json(status);
    }

    private static async Task<IResult> SetSecretRef(
        string name, SetIntegrationSecretRequest request, ClaimsPrincipal user,
        IIntegrationSettingsStore settings, IAuditWriter audit, TimeProvider clock, CancellationToken ct)
    {
        if (!KnownIntegrations.ContainsKey(name))
            return Results.NotFound();

        if (name == "obsidian")
        {
            return Results.BadRequest(new
            {
                message = "The obsidian integration is filesystem-backed (Orchestrator:Vault:Root) " +
                          "and uses no secret reference.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.SecretRef))
            return Results.BadRequest(new { message = "A secret reference is required." });

        // The single guard that keeps a real credential from ever being persisted here:
        // the same shape detector AuditWriter uses to keep secret values out of the
        // audit log.
        if (SecretShapeDetector.IsSecretShape(request.SecretRef))
        {
            return Results.BadRequest(new
            {
                message = "The value looks like a secret value, not a reference. Store the secret " +
                           "with the secret store and submit its reference key instead.",
            });
        }

        SecretRef secretRef;
        try
        {
            secretRef = new SecretRef(request.SecretRef);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }

        var now = clock.GetUtcNow();
        await settings.SetSecretRefAsync(name, secretRef.Key, now, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: Actor(user),
            TaskId: null,
            Action: "integration.configure",
            Reason: $"Integration '{name}' configured to use secret reference '{secretRef.Key}'.",
            Outcome: "Success",
            CorrelationId: Guid.CreateVersion7().ToString(),
            Details: new Dictionary<string, string> { ["name"] = name, ["secretRef"] = secretRef.Key }), ct);

        return Results.NoContent();
    }

    private static async Task<IntegrationStatus> BuildStatusAsync(
        string name, string? defaultSecretRef, ISecretStore secrets, IIntegrationSettingsStore settings,
        VaultOptions vault, CancellationToken ct)
    {
        if (name == "obsidian")
            return await BuildObsidianStatusAsync(vault, settings, ct);

        var setting = await settings.GetAsync(name, ct);
        var secretRef = setting?.SecretRef ?? defaultSecretRef;

        if (secretRef is null)
        {
            return new IntegrationStatus(name, Configured: false, SecretFingerprint: "",
                setting?.LastCheckedAt, setting?.Health ?? "NotConfigured", setting?.Detail);
        }

        var description = await secrets.DescribeAsync(new SecretRef(secretRef), ct);
        return new IntegrationStatus(
            name,
            Configured: description.Exists,
            SecretFingerprint: description.Fingerprint,
            setting?.LastCheckedAt,
            setting?.Health ?? "Unknown",
            setting?.Detail);
    }

    /// <summary>
    /// Obsidian's status comes from <see cref="VaultOptions"/>, not the secret
    /// store: configured means a vault root is set, and the detail names the
    /// effective root. A recorded test result (health/detail) takes precedence
    /// when one exists.
    /// </summary>
    private static async Task<IntegrationStatus> BuildObsidianStatusAsync(
        VaultOptions vault, IIntegrationSettingsStore settings, CancellationToken ct)
    {
        var configured = ObsidianIntegrationProbe.IsConfigured(vault.Root);
        var setting = await settings.GetAsync("obsidian", ct);
        return new IntegrationStatus("obsidian", configured, "",
            setting?.LastCheckedAt,
            setting?.Health ?? (configured ? "Unknown" : "NotConfigured"),
            setting?.Detail ?? (configured ? vault.Root : null));
    }

    /// <summary>
    /// A live, read-only connectivity check. P1 has a cheap one (a read-only
    /// transaction) and obsidian probes its vault directory (existence plus
    /// listability, no writes); every other integration honestly reports
    /// NotImplemented rather than fabricating a fake success. Never throws -
    /// any failure becomes an Unhealthy result.
    /// </summary>
    private static async Task<(string Health, string? Detail)> RunLiveCheckAsync(
        string name, string? defaultSecretRef, IIntegrationSettingsStore settings, ISecretStore secrets,
        VaultOptions vault, CancellationToken ct)
    {
        if (name == "obsidian")
            return ObsidianIntegrationProbe.Check(vault.Root);

        if (name != "p1")
        {
            return ("NotImplemented", "No live connectivity check is wired up for this integration yet.");
        }

        var setting = await settings.GetAsync(name, ct);
        var secretRef = setting?.SecretRef ?? defaultSecretRef;
        if (secretRef is null)
            return ("Unhealthy", "No connection is configured for this integration.");

        try
        {
            var connectionString = await secrets.GetAsync(new SecretRef(secretRef), ct);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand("SELECT 1", connection, transaction);
            await command.ExecuteScalarAsync(ct);
            return ("Healthy", null);
        }
        catch (Exception ex)
        {
            return ("Unhealthy", ex.Message);
        }
    }

    private static string Actor(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";
}
