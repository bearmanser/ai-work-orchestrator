using Orchestrator.Api.Configuration;
using Shouldly;

namespace Orchestrator.Integration.Tests;

public sealed class VaultPathResolverTests
{
    [Fact]
    public void Resolve_Empty_ReturnsEmpty()
    {
        VaultPathResolver.Resolve("", "/repo/src/Orchestrator.Api").ShouldBe(string.Empty);
    }

    [Fact]
    public void Resolve_AbsolutePath_ReturnsCanonicalFullPath()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vault-abs"));
        var contentRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "content-root"));

        VaultPathResolver.Resolve(absolute, contentRoot).ShouldBe(absolute);
    }

    [Fact]
    public void Resolve_RelativePath_ResolvesAgainstContentRoot()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "content-root"));

        VaultPathResolver.Resolve(Path.Combine("..", "..", "vault"), contentRoot)
            .ShouldBe(Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "vault")));
    }
}
