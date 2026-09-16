using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Orchestrator.Application.Identity;

namespace Orchestrator.Infrastructure.Identity;

public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int CurrentIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // A fixed, valid-format hash of a password nobody will ever provision. Callers on
    // the login path (Task 12) that need to keep constant work whether or not a login
    // name exists can verify against this instead of short-circuiting, so the observed
    // timing does not reveal account existence.
    public static readonly string DummyHash = HashWithIterations(
        "dummy-password-never-provisioned-used-only-for-timing-safety", CurrentIterations);

    public string Hash(string password) => HashWithIterations(password, CurrentIterations);

    public static string HashWithIterations(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = KeyDerivation.Pbkdf2(password, salt, KeyDerivationPrf.HMACSHA256, iterations, HashBytes);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public PasswordVerificationResult Verify(string storedHash, string password)
    {
        // Parsed defensively: a malformed stored hash (corrupt row, truncated value,
        // unexpected format from a future migration) must return Fail, never throw -
        // an unhandled exception here would turn a bad row into an outage on the login
        // path.
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return PasswordVerificationResult.Fail;
        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0) return PasswordVerificationResult.Fail;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Fail;
        }

        var actual = KeyDerivation.Pbkdf2(password, salt, KeyDerivationPrf.HMACSHA256, iterations, expected.Length);

        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return PasswordVerificationResult.Fail;
        return iterations < CurrentIterations
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }
}
