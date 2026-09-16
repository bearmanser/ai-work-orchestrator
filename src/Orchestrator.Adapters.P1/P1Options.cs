namespace Orchestrator.Adapters.P1;

/// <summary>Options for the P1 named-query adapter.</summary>
/// <param name="CatalogPath">Absolute path to the allowlisted named-query catalog JSON.</param>
public sealed record P1Options(string CatalogPath);
