using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Secrets;
using Shouldly;

namespace Orchestrator.Integration.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string _secretsDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "secret-store-tests-" + Guid.NewGuid())).FullName;

    [Fact]
    public async Task Get_KnownReference_ReturnsValue()
    {
        var store = new FileSecretStore(_secretsDirectory);
        await File.WriteAllTextAsync(Path.Combine(_secretsDirectory, "slack.signing-secret"), "s3cr3t\n");

        (await store.GetAsync(new SecretRef("slack.signing-secret"), default)).ShouldBe("s3cr3t");
    }

    [Fact]
    public async Task Get_UnknownReference_ThrowsSecretNotFoundException()
    {
        var store = new FileSecretStore(_secretsDirectory);

        await Should.ThrowAsync<SecretNotFoundException>(
            () => store.GetAsync(new SecretRef("does-not-exist"), default));
    }

    [Fact]
    public async Task Get_TrimsOnlyTrailingNewline_NotTrailingSpace()
    {
        var store = new FileSecretStore(_secretsDirectory);
        await File.WriteAllTextAsync(Path.Combine(_secretsDirectory, "trailing-space"), "value with space \r\n");

        (await store.GetAsync(new SecretRef("trailing-space"), default)).ShouldBe("value with space ");
    }

    [Fact]
    public async Task Describe_NeverReturnsTheValue()
    {
        var store = new FileSecretStore(_secretsDirectory);
        await File.WriteAllTextAsync(Path.Combine(_secretsDirectory, "adopat"), "supersecretvalue");

        var description = await store.DescribeAsync(new SecretRef("adopat"), default);

        description.Exists.ShouldBeTrue();
        description.Fingerprint.Length.ShouldBe(8);
        description.Fingerprint.ShouldNotContain("supersecret");
    }

    [Fact]
    public async Task Describe_UnknownReference_ReportsDoesNotExist()
    {
        var store = new FileSecretStore(_secretsDirectory);

        var description = await store.DescribeAsync(new SecretRef("missing-key"), default);

        description.Exists.ShouldBeFalse();
        description.LastRotatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Describe_SameValue_ProducesSameFingerprint()
    {
        var store = new FileSecretStore(_secretsDirectory);
        await File.WriteAllTextAsync(Path.Combine(_secretsDirectory, "one"), "shared-value");
        await File.WriteAllTextAsync(Path.Combine(_secretsDirectory, "two"), "shared-value");

        var one = await store.DescribeAsync(new SecretRef("one"), default);
        var two = await store.DescribeAsync(new SecretRef("two"), default);

        one.Fingerprint.ShouldBe(two.Fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Spaces")]
    [InlineData("UPPER")]
    [InlineData("../escape")]
    public void SecretRef_InvalidKey_Throws(string key) =>
        Should.Throw<ArgumentException>(() => new SecretRef(key));

    [Fact]
    public async Task EnvironmentSecretStore_MapsKeyToConventionalVariableName()
    {
        const string variableName = "ORCHESTRATOR_SECRET_SLACK_SIGNING_SECRET";
        Environment.SetEnvironmentVariable(variableName, "env-value");
        try
        {
            var store = new EnvironmentSecretStore();

            (await store.GetAsync(new SecretRef("slack.signing-secret"), default)).ShouldBe("env-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task EnvironmentSecretStore_UnknownReference_ThrowsSecretNotFoundException()
    {
        var store = new EnvironmentSecretStore();

        await Should.ThrowAsync<SecretNotFoundException>(
            () => store.GetAsync(new SecretRef("definitely-unset-key"), default));
    }

    public void Dispose()
    {
        Directory.Delete(_secretsDirectory, recursive: true);
    }
}
