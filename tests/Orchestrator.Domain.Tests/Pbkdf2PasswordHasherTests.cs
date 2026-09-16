using Orchestrator.Application.Identity;
using Orchestrator.Infrastructure.Identity;
using Shouldly;

namespace Orchestrator.Domain.Tests;

public class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        _hasher.Hash("correct horse battery staple")
            .ShouldNotBe(_hasher.Hash("correct horse battery staple"));
    }

    [Fact]
    public void Verify_CorrectPassword_Succeeds() =>
        _hasher.Verify(_hasher.Hash("s3cret-passphrase"), "s3cret-passphrase")
            .ShouldBe(PasswordVerificationResult.Success);

    [Fact]
    public void Verify_WrongPassword_Fails() =>
        _hasher.Verify(_hasher.Hash("s3cret-passphrase"), "wrong")
            .ShouldBe(PasswordVerificationResult.Fail);

    [Fact]
    public void Hash_DoesNotContainThePlaintext() =>
        _hasher.Hash("plaintextmarker").ShouldNotContain("plaintextmarker");

    [Fact]
    public void Verify_HashFromOlderIterationCount_RequestsRehash()
    {
        var legacy = Pbkdf2PasswordHasher.HashWithIterations("pw", iterations: 10_000);
        _hasher.Verify(legacy, "pw").ShouldBe(PasswordVerificationResult.SuccessRehashNeeded);
    }

    [Fact]
    public void Verify_MalformedStoredHash_FailsRatherThanThrowing()
    {
        _hasher.Verify("not-a-hash-at-all", "anything").ShouldBe(PasswordVerificationResult.Fail);
        _hasher.Verify("pbkdf2-sha256$abc$salt$hash", "anything").ShouldBe(PasswordVerificationResult.Fail);
        _hasher.Verify("pbkdf2-sha256$210000$not-base64!!$not-base64!!", "anything")
            .ShouldBe(PasswordVerificationResult.Fail);
        _hasher.Verify("", "anything").ShouldBe(PasswordVerificationResult.Fail);
    }
}
