namespace Orchestrator.Api.Configuration;

/// <summary>
/// The resolved vault locations the API was started with: absolute paths, or
/// empty when no vault is configured (in which case the API uses
/// <c>NullVaultStore</c>). Registered as a singleton in <c>Program.cs</c> so
/// endpoints (e.g. the obsidian integration status) can report on the vault
/// without resolving the store itself.
/// </summary>
public sealed record VaultOptions(string Root, string IndexDirectory);
