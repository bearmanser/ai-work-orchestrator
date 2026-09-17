using System.Security.Cryptography;
using System.Text;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Obsidian;

/// <summary>
/// File-system Obsidian vault: notes are <c>{root}/{path}.md</c> files with the six
/// frontmatter properties, guarded by a canonical path-containment check. Writes go
/// to a temp file then move into place atomically after the compare-and-swap check,
/// and every mutation snapshots the result under
/// <c>{root}/.orchestrator-revisions/</c> so it can be reverted without Git on the vault.
/// Search fans out through a rebuildable SQLite index outside the vault, with
/// path-prefix filtering applied before any note file is read.
/// </summary>
public sealed class FileSystemVaultStore : IVaultStore
{
    private const string RevisionsDirectoryName = ".orchestrator-revisions";

    private readonly string _vaultRoot = string.Empty;
    private readonly TimeProvider _clock;
    private readonly VaultSearchIndex _index;
    private readonly List<string> _filesRead = [];
    private readonly Lock _filesReadLock = new();

    public FileSystemVaultStore(string vaultRoot, string indexDirectory, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(clock);
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _clock = clock;
        _index = new VaultSearchIndex(indexDirectory);
        Directory.CreateDirectory(_vaultRoot);
    }

    /// <summary>
    /// Normalized vault-relative paths (forward slashes) read by
    /// <see cref="ReadAsync"/> and <see cref="SearchAsync"/>. Diagnostic
    /// observability proving prefix filtering happens before retrieval.
    /// </summary>
    public IReadOnlyList<string> FilesRead
    {
        get
        {
            lock (_filesReadLock)
                return _filesRead.ToArray();
        }
    }

    public async Task<VaultNote> ReadAsync(string path, CancellationToken ct)
    {
        var (fullPath, relativePath) = ResolvePath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Vault note '{relativePath}' was not found.", fullPath);

        var text = await ReadNoteTextAsync(fullPath, relativePath, ct);
        var (frontmatter, body) = NoteFrontmatterCodec.Parse(text);
        return new VaultNote(
            relativePath, frontmatter, body, HashBody(body),
            new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero));
    }

    public async Task<IReadOnlyList<VaultNote>> SearchAsync(VaultQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var prefixes = query.AllowedPathPrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeRelative)
            .ToArray();
        if (prefixes.Length == 0 || query.MaxResults <= 0)
            return [];

        // Oversample: prefix filtering can only remove candidates, so ask the
        // index for more than MaxResults before filtering down.
        var candidates = await _index.SearchAsync(
            query.Text, checked(query.MaxResults * 2 + 10), ct);

        var notes = new List<VaultNote>();
        foreach (var candidate in candidates)
        {
            if (notes.Count >= query.MaxResults)
                break;
            if (!prefixes.Any(candidate.StartsWith))
                continue;

            VaultNote note;
            try
            {
                note = await ReadAsync(candidate, ct);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(query.Text)
                && !note.Body.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                && !note.Frontmatter.Title.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                continue;

            notes.Add(note);
        }

        return notes;
    }

    public async Task<VaultWriteResult> WriteAsync(VaultWrite write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        NoteFrontmatterCodec.Validate(write.Frontmatter);
        var (fullPath, relativePath) = ResolvePath(write.Path);

        if (File.Exists(fullPath))
        {
            var currentBody = TryParse(await ReadNoteTextAsync(fullPath, relativePath, ct)).Body;
            if (write.ExpectedContentHash is not null
                && !HashesEqual(HashBody(currentBody), write.ExpectedContentHash))
                return VaultWriteResult.Conflict;
        }
        else if (write.ExpectedContentHash is not null)
        {
            return VaultWriteResult.Conflict;
        }

        await WriteNoteTextAsync(fullPath, NoteFrontmatterCodec.Serialize(write.Frontmatter, write.Body), ct);
        SnapshotRevision(relativePath);
        await _index.UpsertAsync(relativePath, IndexContent(write.Frontmatter, write.Body), ct);
        return VaultWriteResult.Written;
    }

    public async Task<VaultWriteResult> AppendOrReplaceSectionAsync(
        string path, string sectionHeading, string sectionBody, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionHeading);
        ArgumentNullException.ThrowIfNull(sectionBody);

        var (fullPath, relativePath) = ResolvePath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Vault note '{relativePath}' was not found.", fullPath);

        var text = await ReadNoteTextAsync(fullPath, relativePath, ct);

        // A legacy note without a frontmatter block is edited as raw text.
        var (frontmatter, body) = TryParse(text);

        var updatedBody = ReplaceSection(body, sectionHeading.Trim(), sectionBody);
        var updatedText = frontmatter is null
            ? updatedBody
            : NoteFrontmatterCodec.Serialize(frontmatter, updatedBody);
        await WriteNoteTextAsync(fullPath, updatedText, ct);

        SnapshotRevision(relativePath);
        await _index.UpsertAsync(
            relativePath,
            frontmatter is null ? updatedBody : IndexContent(frontmatter, updatedBody),
            ct);
        return VaultWriteResult.Written;
    }

    public async Task<IReadOnlyList<VaultRevision>> GetRevisionsAsync(string path, CancellationToken ct)
    {
        var (_, relativePath) = ResolvePath(path);
        var directory = RevisionDirectory(relativePath);
        if (!Directory.Exists(directory))
            return [];

        var revisions = new List<VaultRevision>();
        foreach (var file in Directory.GetFiles(directory, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(file, ct);
            var body = TryParse(text).Body;

            revisions.Add(new VaultRevision(
                Path.GetFileNameWithoutExtension(file),
                new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero),
                HashBody(body)));
        }

        return revisions;
    }

    public async Task RevertAsync(string path, string revisionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        if (revisionId.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Invalid revision id.", nameof(revisionId));

        var (fullPath, relativePath) = ResolvePath(path);
        var revisionFile = Path.GetFullPath(Path.Combine(RevisionDirectory(relativePath), revisionId + ".md"));
        var directory = RevisionDirectory(relativePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (revisionFile != directory
            && !revisionFile.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Revision resolves outside the revisions directory.", nameof(revisionId));
        if (!File.Exists(revisionFile))
            throw new FileNotFoundException($"Revision '{revisionId}' was not found.", revisionFile);

        var text = await File.ReadAllTextAsync(revisionFile, ct);
        await WriteNoteTextAsync(fullPath, text, ct);

        var (frontmatter, body) = TryParse(text);

        SnapshotRevision(relativePath);
        await _index.UpsertAsync(
            relativePath,
            frontmatter is null ? body : IndexContent(frontmatter, body),
            ct);
    }

    internal static string HashBody(string body) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static bool HashesEqual(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    private static string IndexContent(NoteFrontmatter frontmatter, string body) =>
        frontmatter.Title + "\n" + string.Join(" ", frontmatter.Tags) + "\n" + body;

    /// <summary>
    /// Resolves a vault-relative path, rejecting anything that escapes the vault root.
    /// </summary>
    private (string FullPath, string RelativePath) ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Vault path must not be empty.", nameof(path));

        var candidate = Path.GetFullPath(
            Path.Combine(_vaultRoot, NormalizeRelative(path).Replace('/', Path.DirectorySeparatorChar)));
        var root = _vaultRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (candidate != root
            && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"'{path}' resolves outside the vault root.");

        return (candidate, NormalizeRelative(path));
    }

    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    private async Task<string> ReadNoteTextAsync(string fullPath, string relativePath, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(fullPath, ct);
        lock (_filesReadLock)
            _filesRead.Add(relativePath);
        return text;
    }

    /// <summary>Writes via a temp file moved into place, so a crash cannot leave a half-written note.</summary>
    private static async Task WriteNoteTextAsync(string fullPath, string text, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        var tmpPath = Path.Combine(
            directory ?? Path.GetTempPath(), ".tmp-" + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(tmpPath, text, Encoding.UTF8, ct);
        File.Move(tmpPath, fullPath, overwrite: true);
    }

    private string RevisionDirectory(string relativePath)
    {
        var pathHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)));
        return Path.Combine(_vaultRoot, RevisionsDirectoryName, pathHash);
    }

    /// <summary>
    /// Copies the just-written note into the revisions directory. The revision id
    /// embeds the unix time plus a process-wide monotonic sequence so filenames
    /// sort chronologically even when two writes land in the same second (a GUID
    /// suffix alone would order same-second snapshots randomly); the GUID keeps
    /// ids unique and the file timestamp carries the vault clock time.
    /// </summary>
    private static long _revisionSequence;

    private void SnapshotRevision(string relativePath)
    {
        var now = _clock.GetUtcNow();
        var sequence = Interlocked.Increment(ref _revisionSequence);
        var revisionId = $"{now.ToUnixTimeSeconds():D11}-{sequence:D10}-{Guid.NewGuid():N}";
        var directory = RevisionDirectory(relativePath);
        Directory.CreateDirectory(directory);

        var revisionFile = Path.Combine(directory, revisionId + ".md");
        File.Copy(ResolvePath(relativePath).FullPath, revisionFile, overwrite: false);
        File.SetLastWriteTimeUtc(revisionFile, now.UtcDateTime);
    }

    private static (NoteFrontmatter? Frontmatter, string Body) TryParse(string text)
    {
        try
        {
            return NoteFrontmatterCodec.Parse(text);
        }
        catch (InvalidDataException)
        {
            return (null, text);
        }
    }

    private static string ReplaceSection(string body, string heading, string sectionBody)
    {
        var marker = "## " + heading;
        var normalized = body.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimEnd() == marker);
        var block = marker + "\n\n" + sectionBody.Replace("\r\n", "\n").TrimEnd('\n');

        if (start < 0)
        {
            var existing = normalized.TrimEnd('\n');
            return (existing.Length == 0 ? string.Empty : existing + "\n\n") + block + "\n";
        }

        var end = start + 1;
        while (end < lines.Length && !lines[end].StartsWith("## ", StringComparison.Ordinal))
            end++;

        var builder = new StringBuilder();
        var before = string.Join('\n', lines[..start]).TrimEnd('\n');
        if (before.Length > 0)
            builder.Append(before).Append("\n\n");
        builder.Append(block).Append('\n');
        var after = string.Join('\n', lines[end..]).TrimStart('\n');
        if (after.Length > 0)
            builder.Append(after).Append('\n');
        return builder.ToString();
    }
}
