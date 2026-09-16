using System.Text.Json;
using Orchestrator.Domain.Authorization;

namespace Orchestrator.Domain.Repositories;

/// <summary>
/// The orchestrator's working knowledge of one configured repository: identity,
/// access policy, build/test commands, and when it was last inspected.
/// </summary>
public sealed class RepositoryProfile
{
    private RepositoryProfile() { }

    public RepositoryProfile(
        string name, string remoteUrl, string defaultBranch, string purpose,
        string ownerContactId, RepositoryAccess accessPolicy, BuildCommands buildCommands,
        IReadOnlyList<string> instructionFilePaths)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new ArgumentException("Remote URL is required.", nameof(remoteUrl));
        if (string.IsNullOrWhiteSpace(defaultBranch))
            throw new ArgumentException("Default branch is required.", nameof(defaultBranch));
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("Purpose is required.", nameof(purpose));
        if (string.IsNullOrWhiteSpace(ownerContactId))
            throw new ArgumentException("Owner contact is required.", nameof(ownerContactId));
        ArgumentNullException.ThrowIfNull(buildCommands);

        Id = Guid.CreateVersion7();
        Name = name;
        RemoteUrl = remoteUrl;
        DefaultBranch = defaultBranch;
        Purpose = purpose;
        OwnerContactId = ownerContactId;
        AccessPolicy = accessPolicy;
        BuildCommands = buildCommands;
        InstructionFilePathsJson = JsonSerializer.Serialize(instructionFilePaths);
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string RemoteUrl { get; private set; } = null!;
    public string DefaultBranch { get; private set; } = null!;
    public string Purpose { get; private set; } = null!;
    public string OwnerContactId { get; private set; } = null!;
    public RepositoryAccess AccessPolicy { get; private set; }
    public BuildCommands BuildCommands { get; private set; } = null!;
    public string InstructionFilePathsJson { get; private set; } = null!;
    public string? LastInspectedCommit { get; private set; }
    public DateTimeOffset? LastInspectedAt { get; private set; }
    public uint Version { get; private set; }

    public IReadOnlyList<string> InstructionFilePaths() =>
        JsonSerializer.Deserialize<List<string>>(InstructionFilePathsJson) ?? [];

    public void ApplyUpdate(
        string name, string defaultBranch, string purpose, string ownerContactId,
        BuildCommands? buildCommands, IReadOnlyList<string> instructionFilePaths)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(defaultBranch))
            throw new ArgumentException("Default branch is required.", nameof(defaultBranch));
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("Purpose is required.", nameof(purpose));
        if (string.IsNullOrWhiteSpace(ownerContactId))
            throw new ArgumentException("Owner contact is required.", nameof(ownerContactId));

        Name = name;
        DefaultBranch = defaultBranch;
        Purpose = purpose;
        OwnerContactId = ownerContactId;
        if (buildCommands is not null)
            BuildCommands = buildCommands;
        InstructionFilePathsJson = JsonSerializer.Serialize(instructionFilePaths);
    }

    public void RecordInspection(string commit, DateTimeOffset at)
    {
        LastInspectedCommit = commit;
        LastInspectedAt = at.ToUniversalTime();
    }
}
