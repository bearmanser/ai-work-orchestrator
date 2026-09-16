namespace Orchestrator.Application.Identity;

public enum PasswordVerificationResult
{
    Fail,
    Success,
    SuccessRehashNeeded,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationResult Verify(string storedHash, string password);
}
