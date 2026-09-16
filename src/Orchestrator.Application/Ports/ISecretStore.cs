using System.Text.RegularExpressions;

namespace Orchestrator.Application.Ports;

/// <summary>
/// A validated reference to a secret. The system persists only this reference,
/// never the secret value itself.
/// </summary>
public readonly partial record struct SecretRef
{
    public SecretRef(string key)
    {
        if (string.IsNullOrEmpty(key) || !KeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                $"'{key}' is not a valid secret key. Keys must match ^[a-z0-9][a-z0-9._-]{{2,99}}$.",
                nameof(key));
        }

        Key = key;
    }

    public string Key { get; }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,99}$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// Metadata about a secret that never carries the value itself.
/// </summary>
/// <param name="Exists">Whether the referenced secret is currently present.</param>
/// <param name="LastRotatedAt">When the secret was last rotated, if known.</param>
/// <param name="Fingerprint">
/// The first 8 hex characters of SHA256(value) — enough to confirm two references
/// point at the same secret, useless for recovering the value.
/// </param>
public sealed record SecretDescription(bool Exists, DateTimeOffset? LastRotatedAt, string Fingerprint);

/// <summary>Thrown when a referenced secret is not present in the backing store.</summary>
public sealed class SecretNotFoundException(string key)
    : Exception($"Secret '{key}' was not found.")
{
    public string Key { get; } = key;
}

/// <summary>
/// Resolves secret references to their values. Implementations must never persist
/// or log the resolved value; only the <see cref="SecretRef"/> is safe to store.
/// </summary>
public interface ISecretStore
{
    /// <summary>Resolves a secret's value. Throws <see cref="SecretNotFoundException"/> when absent.</summary>
    Task<string> GetAsync(SecretRef reference, CancellationToken ct);

    /// <summary>Describes a secret without ever returning its value.</summary>
    Task<SecretDescription> DescribeAsync(SecretRef reference, CancellationToken ct);
}
