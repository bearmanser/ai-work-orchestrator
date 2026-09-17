using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Obsidian;

/// <summary>
/// <see cref="IVaultStore"/> used when no vault is configured: every search is
/// empty, every read fails. Lets context assembly run with zero memory evidence
/// instead of failing host startup for deployments without a vault.
/// </summary>
public sealed class NullVaultStore : IVaultStore
{
    public Task<IReadOnlyList<VaultNote>> SearchAsync(VaultQuery query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VaultNote>>([]);

    public Task<VaultNote> ReadAsync(string path, CancellationToken ct) =>
        throw new FileNotFoundException($"No vault is configured; cannot read '{path}'.");

    public Task<VaultWriteResult> WriteAsync(VaultWrite write, CancellationToken ct) =>
        throw new InvalidOperationException("No vault is configured.");

    public Task<VaultWriteResult> AppendOrReplaceSectionAsync(
        string path, string sectionHeading, string sectionBody, CancellationToken ct) =>
        throw new InvalidOperationException("No vault is configured.");

    public Task<IReadOnlyList<VaultRevision>> GetRevisionsAsync(string path, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VaultRevision>>([]);

    public Task RevertAsync(string path, string revisionId, CancellationToken ct) =>
        throw new InvalidOperationException("No vault is configured.");
}
