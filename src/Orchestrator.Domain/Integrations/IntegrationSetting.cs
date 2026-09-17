namespace Orchestrator.Domain.Integrations;

/// <summary>
/// Admin-configured metadata for one named integration: which secret reference it
/// should resolve (never the secret value itself) and the result of the most recent
/// live connectivity test. A row is created lazily, the first time an admin configures
/// the integration or triggers a test - an integration with no row yet simply uses its
/// built-in default secret reference and reports as never tested.
/// </summary>
public sealed class IntegrationSetting
{
    private IntegrationSetting() { } // EF

    public IntegrationSetting(string name, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        Name = name;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    public string Name { get; private set; } = null!;

    /// <summary>The <c>SecretRef</c> key an admin configured, overriding the built-in default. Never a secret value.</summary>
    public string? SecretRef { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }
    public string? Health { get; private set; }
    public string? Detail { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void SetSecretRef(string secretRef, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new ArgumentException("Secret reference is required.", nameof(secretRef));

        SecretRef = secretRef;
        UpdatedAt = at.ToUniversalTime();
    }

    public void RecordTestResult(string health, string? detail, DateTimeOffset at)
    {
        Health = health;
        Detail = detail;
        LastCheckedAt = at.ToUniversalTime();
        UpdatedAt = at.ToUniversalTime();
    }
}
