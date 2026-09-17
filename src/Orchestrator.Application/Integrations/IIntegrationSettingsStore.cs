namespace Orchestrator.Application.Integrations;

/// <summary>
/// The admin-configured state of one named integration, as read back from storage.
/// Never carries a secret value - only a reference key and the outcome of the most
/// recent live test.
/// </summary>
public sealed record IntegrationSettingRecord(
    string Name,
    string? SecretRef,
    DateTimeOffset? LastCheckedAt,
    string? Health,
    string? Detail);

/// <summary>
/// Persists per-integration admin configuration: the secret reference to use (never
/// the secret value) and the result of the most recent connectivity test.
/// </summary>
public interface IIntegrationSettingsStore
{
    Task<IntegrationSettingRecord?> GetAsync(string name, CancellationToken ct);

    Task SetSecretRefAsync(string name, string secretRef, DateTimeOffset at, CancellationToken ct);

    Task RecordTestResultAsync(string name, string health, string? detail, DateTimeOffset at, CancellationToken ct);
}
