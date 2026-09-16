namespace Orchestrator.Application.Ports;

/// <summary>
/// The vault's six frontmatter properties, always serialized in this order:
/// <c>title</c>, <c>type</c>, <c>area</c>, <c>status</c>, <c>updated</c>, <c>tags</c>.
/// Writing a note with any of them missing is a hard error.
/// </summary>
public sealed record NoteFrontmatter(
    string Title,
    string Type,
    string Area,
    string Status,
    DateTimeOffset Updated,
    IReadOnlyList<string> Tags);

/// <summary>A single note read back from the vault.</summary>
public sealed record VaultNote(
    string Path,
    NoteFrontmatter Frontmatter,
    string Body,
    string ContentHash,
    DateTimeOffset ModifiedAt);

/// <summary>A vault search request. Only notes under <c>AllowedPathPrefixes</c> are considered.</summary>
public sealed record VaultQuery(string Text, IReadOnlyList<string> AllowedPathPrefixes, int MaxResults = 20);

/// <summary>
/// A note to write. <c>ExpectedContentHash</c> is the compare-and-swap token:
/// when set, the write only proceeds if the stored content still hashes to it.
/// </summary>
public sealed record VaultWrite(
    string Path,
    NoteFrontmatter Frontmatter,
    string Body,
    string? ExpectedContentHash = null);

/// <summary>Outcome of a vault write.</summary>
public enum VaultWriteResult
{
    Written = 0,
    Conflict = 1,
}

/// <summary>A reversible snapshot of a note's previous content.</summary>
public sealed record VaultRevision(string RevisionId, DateTimeOffset SavedAt, string ContentHash);

/// <summary>
/// Obsidian vault access: prefix-filtered search, guarded reads, atomic
/// compare-and-swap writes with section-preserving updates, and reversible revisions.
/// </summary>
public interface IVaultStore
{
    /// <summary>
    /// Searches notes whose path is under one of <c>AllowedPathPrefixes</c>.
    /// Prefix filtering happens before retrieval: notes outside the prefixes are never read.
    /// </summary>
    Task<IReadOnlyList<VaultNote>> SearchAsync(VaultQuery query, CancellationToken ct);

    /// <summary>Reads a note. Throws <see cref="UnauthorizedAccessException"/> when the path escapes the vault root.</summary>
    Task<VaultNote> ReadAsync(string path, CancellationToken ct);

    /// <summary>
    /// Writes a note atomically. Returns <see cref="VaultWriteResult.Conflict"/> and writes
    /// nothing when <c>ExpectedContentHash</c> is set but stale.
    /// </summary>
    Task<VaultWriteResult> WriteAsync(VaultWrite write, CancellationToken ct);

    /// <summary>Replaces (or appends) a single <c>##</c> section, preserving unrelated sections.</summary>
    Task<VaultWriteResult> AppendOrReplaceSectionAsync(
        string path, string sectionHeading, string sectionBody, CancellationToken ct);

    /// <summary>Lists recorded revisions for a note, oldest first.</summary>
    Task<IReadOnlyList<VaultRevision>> GetRevisionsAsync(string path, CancellationToken ct);

    /// <summary>Restores a note to a previously recorded revision.</summary>
    Task RevertAsync(string path, string revisionId, CancellationToken ct);
}
