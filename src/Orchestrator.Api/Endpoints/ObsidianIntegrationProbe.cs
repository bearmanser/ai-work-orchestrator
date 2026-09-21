namespace Orchestrator.Api.Endpoints;

/// <summary>
/// Read-only health probe for the filesystem-backed Obsidian vault. Unlike the
/// secret-backed integrations, obsidian has no credential to fingerprint:
/// configured means a vault root is set, healthy means the directory exists
/// and can be listed. Never writes to the vault.
/// </summary>
public static class ObsidianIntegrationProbe
{
    public static bool IsConfigured(string? vaultRoot) => !string.IsNullOrWhiteSpace(vaultRoot);

    public static (string Health, string? Detail) Check(string? vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
            return ("Unhealthy", "Vault is not configured (Orchestrator:Vault:Root is empty).");

        try
        {
            // Enumerating proves the directory exists and is listable; the
            // entries themselves are irrelevant to health.
            _ = Directory.GetFileSystemEntries(vaultRoot);
            return ("Healthy", vaultRoot);
        }
        catch (Exception ex)
        {
            return ("Unhealthy", ex.Message);
        }
    }
}
