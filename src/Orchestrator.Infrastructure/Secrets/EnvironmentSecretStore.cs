using System.Security.Cryptography;
using System.Text;
using Orchestrator.Application.Ports;

namespace Orchestrator.Infrastructure.Secrets;

/// <summary>
/// Resolves secrets from environment variables, for local development.
/// Maps a key such as <c>slack.signing-secret</c> to the environment variable
/// <c>ORCHESTRATOR_SECRET_SLACK_SIGNING_SECRET</c>.
/// </summary>
public sealed class EnvironmentSecretStore : ISecretStore
{
    private const string Prefix = "ORCHESTRATOR_SECRET_";

    public Task<string> GetAsync(SecretRef reference, CancellationToken ct)
    {
        var value = Environment.GetEnvironmentVariable(ToVariableName(reference));
        if (value is null)
            throw new SecretNotFoundException(reference.Key);

        return Task.FromResult(value);
    }

    public Task<SecretDescription> DescribeAsync(SecretRef reference, CancellationToken ct)
    {
        var value = Environment.GetEnvironmentVariable(ToVariableName(reference));
        var description = value is null
            ? new SecretDescription(Exists: false, LastRotatedAt: null, Fingerprint: string.Empty)
            : new SecretDescription(Exists: true, LastRotatedAt: null, Fingerprint: Fingerprint(value));

        return Task.FromResult(description);
    }

    private static string ToVariableName(SecretRef reference) =>
        Prefix + reference.Key.Replace('.', '_').Replace('-', '_').ToUpperInvariant();

    private static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..8];
    }
}
