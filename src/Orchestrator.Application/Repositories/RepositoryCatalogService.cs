using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Repositories;

/// <summary>Configured git hosts a repository remote may point at.</summary>
public sealed class GitCatalogOptions
{
    public string[] AllowedHosts { get; set; } = ["dev.azure.com", "kameleon.visualstudio.com"];
}

public sealed record CreateRepositoryRequest(
    string Name,
    string RemoteUrl,
    string DefaultBranch,
    string Purpose,
    string OwnerContactId,
    RepositoryAccess AccessPolicy,
    BuildCommands BuildCommands,
    IReadOnlyList<string> InstructionFilePaths);

public sealed record UpdateRepositoryRequest(
    string Name,
    string Purpose,
    string DefaultBranch,
    string OwnerContactId,
    BuildCommands? BuildCommands,
    IReadOnlyList<string> InstructionFilePaths);

/// <summary>Thrown when an update carries a stale `If-Match` version.</summary>
public sealed class ConcurrentModificationException(Guid id, uint expectedVersion, Exception? inner = null)
    : InvalidOperationException($"Repository '{id}' was modified; expected version {expectedVersion}.", inner)
{
    public Guid Id { get; } = id;
    public uint ExpectedVersion { get; } = expectedVersion;
}

/// <summary>
/// Manages the repository catalog: validated creation, version-checked updates,
/// and an audit trail for every change.
/// </summary>
public sealed class RepositoryCatalogService(
    IRepositoryProfileStore store,
    IAuditWriter audit,
    GitCatalogOptions options)
{
    public async Task<RepositoryProfile> CreateAsync(
        CreateRepositoryRequest request, string actor, CancellationToken ct)
    {
        var remoteUrl = NormalizeRemoteUrl(request.RemoteUrl, options.AllowedHosts);

        if (await store.RemoteExistsAsync(remoteUrl, ct))
            throw new InvalidOperationException($"A repository with remote '{remoteUrl}' already exists.");

        var profile = new RepositoryProfile(
            request.Name, remoteUrl, request.DefaultBranch, request.Purpose,
            request.OwnerContactId, request.AccessPolicy, request.BuildCommands,
            request.InstructionFilePaths);
        await store.CreateAsync(profile, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: null,
            Action: "repository.create",
            Reason: $"Repository '{profile.Name}' added to the catalog.",
            Outcome: "Success",
            CorrelationId: profile.Id.ToString(),
            Details: new Dictionary<string, string>
            {
                ["name"] = profile.Name,
                ["remoteUrl"] = profile.RemoteUrl,
            }), ct);

        return profile;
    }

    public async Task<RepositoryProfile> UpdateAsync(
        Guid id, UpdateRepositoryRequest request, uint expectedVersion, string actor, CancellationToken ct)
    {
        var profile = await store.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Repository '{id}' was not found.");

        if (profile.Version != expectedVersion)
            throw new ConcurrentModificationException(id, expectedVersion);

        profile.ApplyUpdate(
            request.Name, request.DefaultBranch, request.Purpose, request.OwnerContactId,
            request.BuildCommands, request.InstructionFilePaths);
        await store.UpdateAsync(profile, expectedVersion, ct);

        await audit.RecordAsync(new AuditEntry(
            Actor: actor,
            TaskId: null,
            Action: "repository.update",
            Reason: $"Repository '{profile.Name}' updated.",
            Outcome: "Success",
            CorrelationId: profile.Id.ToString(),
            Details: new Dictionary<string, string> { ["name"] = profile.Name }), ct);

        return profile;
    }

    public Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken ct) =>
        store.ListAsync(ct);

    public Task<RepositoryProfile?> GetAsync(Guid id, CancellationToken ct) =>
        store.GetByIdAsync(id, ct);

    /// <summary>
    /// Validates the remote is an absolute https URL on a configured host and
    /// normalizes it (lowercase host, no trailing slash or `.git`) so the unique
    /// index catches duplicates regardless of spelling.
    /// </summary>
    internal static string NormalizeRemoteUrl(string remoteUrl, IReadOnlyList<string> allowedHosts)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Remote '{remoteUrl}' must be an absolute https URL.", nameof(remoteUrl));

        if (!allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Remote host '{uri.Host}' is not in the configured hosts.", nameof(remoteUrl));

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        if (path.Length <= 1)
            throw new ArgumentException($"Remote '{remoteUrl}' does not point at a repository.", nameof(remoteUrl));

        return $"https://{uri.Host.ToLowerInvariant()}{path}";
    }
}
