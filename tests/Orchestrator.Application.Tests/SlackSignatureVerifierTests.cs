using System.Security.Cryptography;
using System.Text;
using Orchestrator.Adapters.Slack;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class SlackSignatureVerifierTests
{
    private const string Secret = "8f742231b10e8888abcd99yyyzzz85a5";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Verify_CorrectSignature_IsTrue()
    {
        var body = """{"type":"event_callback"}""";
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var signature = ComputeExpected(Secret, timestamp, body);

        SlackSignatureVerifier.Verify(Secret, timestamp, signature, body, Now).ShouldBeTrue();
    }

    [Fact]
    public void Verify_TamperedBody_IsFalse()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var signature = ComputeExpected(Secret, timestamp, """{"type":"event_callback"}""");

        SlackSignatureVerifier.Verify(Secret, timestamp, signature, """{"type":"evil"}""", Now).ShouldBeFalse();
    }

    [Fact]
    public void Verify_TimestampOlderThanFiveMinutes_IsFalse()
    {
        var oldTimestamp = Now.AddMinutes(-6).ToUnixTimeSeconds().ToString();
        var body = """{"type":"event_callback"}""";

        SlackSignatureVerifier.Verify(Secret, oldTimestamp, ComputeExpected(Secret, oldTimestamp, body), body, Now)
            .ShouldBeFalse();
    }

    [Fact]
    public void Verify_TimestampInTheFuture_IsFalse()
    {
        var future = Now.AddMinutes(6).ToUnixTimeSeconds().ToString();
        var body = "{}";
        SlackSignatureVerifier.Verify(Secret, future, ComputeExpected(Secret, future, body), body, Now).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-signature")]
    [InlineData("v1=abc")]
    public void Verify_MalformedSignatureHeader_IsFalse(string signature) =>
        SlackSignatureVerifier.Verify(Secret, Now.ToUnixTimeSeconds().ToString(), signature, "{}", Now).ShouldBeFalse();

    [Fact]
    public void Verify_WrongSecret_IsFalse()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var body = "{}";
        SlackSignatureVerifier.Verify(Secret, timestamp, ComputeExpected("other-secret", timestamp, body), body, Now)
            .ShouldBeFalse();
    }

    private static string ComputeExpected(string secret, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}"));
        return "v0=" + Convert.ToHexStringLower(hash);
    }
}
