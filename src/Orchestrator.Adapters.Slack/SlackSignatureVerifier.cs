using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Adapters.Slack;

/// <summary>
/// Verifies Slack request signatures: HMAC-SHA256 over <c>v0:timestamp:body</c>,
/// compared in constant time. Rejects missing or malformed <c>v0=</c> signatures
/// and timestamps outside a five-minute skew window to block replays.
/// </summary>
public static class SlackSignatureVerifier
{
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static bool Verify(string signingSecret, string? timestampHeader, string? signatureHeader,
                              string rawBody, DateTimeOffset now)
    {
        if (!long.TryParse(timestampHeader, out var unix)) return false;
        var sent = DateTimeOffset.FromUnixTimeSeconds(unix);
        if (sent > now + MaxSkew || sent < now - MaxSkew) return false;
        if (signatureHeader is null || !signatureHeader.StartsWith("v0=", StringComparison.Ordinal)) return false;

        var basestring = $"v0:{timestampHeader}:{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var computed = "v0=" + Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(basestring)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(signatureHeader));
    }
}
