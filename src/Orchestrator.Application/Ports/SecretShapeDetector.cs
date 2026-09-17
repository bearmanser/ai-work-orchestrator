using System.Text.RegularExpressions;

namespace Orchestrator.Application.Ports;

/// <summary>
/// The single place that decides whether a free-text value looks like a real credential
/// (a GitHub PAT, a Slack token, an OpenAI-style API key, a JWT) rather than a reference
/// to one. Shared by <see cref="Orchestrator.Application.Ports.IAuditWriter"/>'s guard on
/// audit details and by the admin integrations endpoint's guard on a submitted secret
/// reference, so both places stay consistent - a value that would be rejected in one
/// place is rejected in the other.
/// </summary>
public static partial class SecretShapeDetector
{
    // Matches the shape of common credential/token formats (GitHub PATs, Slack tokens,
    // OpenAI-style API keys, JWTs) so a caller can never persist a secret value into the
    // audit log or the integration settings store - only a reference to one. Deliberately
    // does not match ordinary values such as repository names, commit SHAs, branch names,
    // secret-reference keys (e.g. "azuredevops.pat"), or base64-ish hashes.
    [GeneratedRegex(@"(gh[pousr]_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9]{20,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.)")]
    private static partial Regex SecretShape();

    /// <summary>True when <paramref name="value"/> looks like a secret value rather than a reference.</summary>
    public static bool IsSecretShape(string value) => SecretShape().IsMatch(value);

    /// <summary>Replaces every secret-shaped span in <paramref name="value"/> with a redaction placeholder.</summary>
    public static string Redact(string value, string placeholder = "[redacted]") =>
        SecretShape().Replace(value, placeholder);
}
