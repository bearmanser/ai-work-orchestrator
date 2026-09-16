using System.Security.Cryptography;
using System.Text;
using Orchestrator.Application.Ports;

namespace Orchestrator.Infrastructure.Secrets;

/// <summary>
/// Resolves secrets from files on disk, one file per key, named after the key.
/// Matches the Docker secrets convention (<c>/run/secrets/&lt;key&gt;</c>) so production
/// can mount the secrets directory unchanged.
/// </summary>
public sealed class FileSecretStore(string secretsDirectory) : ISecretStore
{
    private readonly string _secretsDirectory = Path.GetFullPath(secretsDirectory);

    public async Task<string> GetAsync(SecretRef reference, CancellationToken ct)
    {
        var path = ResolvePath(reference);
        if (!File.Exists(path))
            throw new SecretNotFoundException(reference.Key);

        var raw = await File.ReadAllTextAsync(path, ct);
        return TrimTrailingNewline(raw);
    }

    public async Task<SecretDescription> DescribeAsync(SecretRef reference, CancellationToken ct)
    {
        var path = ResolvePath(reference);
        if (!File.Exists(path))
            return new SecretDescription(Exists: false, LastRotatedAt: null, Fingerprint: string.Empty);

        var value = TrimTrailingNewline(await File.ReadAllTextAsync(path, ct));
        var lastRotatedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return new SecretDescription(Exists: true, LastRotatedAt: lastRotatedAt, Fingerprint: Fingerprint(value));
    }

    /// <summary>
    /// Resolves the on-disk path for a key. Even though <see cref="SecretRef"/> already
    /// rejects path-traversal characters, this resolves and verifies the result stays
    /// inside <see cref="_secretsDirectory"/> so a crafted key can never escape it.
    /// </summary>
    private string ResolvePath(SecretRef reference)
    {
        var candidate = Path.GetFullPath(Path.Combine(_secretsDirectory, reference.Key));
        var root = _secretsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (candidate != root && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"'{reference.Key}' resolves outside the secrets directory.", nameof(reference));

        return candidate;
    }

    private static string TrimTrailingNewline(string value) => value.TrimEnd('\r', '\n');

    private static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..8];
    }
}
