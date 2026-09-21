using Orchestrator.Api.Endpoints;
using Shouldly;

namespace Orchestrator.Integration.Tests;

public sealed class ObsidianIntegrationProbeTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "obsidian-probe-tests-" + Guid.NewGuid())).FullName;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsConfigured_BlankRoot_ReturnsFalse(string root)
    {
        ObsidianIntegrationProbe.IsConfigured(root).ShouldBeFalse();
    }

    [Fact]
    public void IsConfigured_NonEmptyRoot_ReturnsTrue()
    {
        ObsidianIntegrationProbe.IsConfigured(Path.Combine(_root, "vault")).ShouldBeTrue();
    }

    [Fact]
    public void Check_ExistingDirectory_ReturnsHealthyWithRootAsDetail()
    {
        var vault = Directory.CreateDirectory(Path.Combine(_root, "vault")).FullName;

        var (health, detail) = ObsidianIntegrationProbe.Check(vault);

        health.ShouldBe("Healthy");
        detail.ShouldBe(vault);
    }

    [Fact]
    public void Check_MissingDirectory_ReturnsUnhealthy()
    {
        var (health, detail) = ObsidianIntegrationProbe.Check(Path.Combine(_root, "no-such-vault"));

        health.ShouldBe("Unhealthy");
        detail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Check_BlankRoot_ReturnsUnhealthyMentioningConfiguration()
    {
        var (health, detail) = ObsidianIntegrationProbe.Check("  ");

        health.ShouldBe("Unhealthy");
        detail.ShouldNotBeNull();
        detail.ShouldContain("not configured");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone; nothing to clean up.
        }
    }
}
