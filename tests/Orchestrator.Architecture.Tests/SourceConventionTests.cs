using Shouldly;

namespace Orchestrator.Architecture.Tests;

public class SourceConventionTests
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orchestrator.sln")))
            dir = dir.Parent;
        dir.ShouldNotBeNull("Orchestrator.sln not found above the test output directory");
        return Path.Combine(dir!.FullName, "src");
    }

    [Theory]
    [InlineData("DateTime.UtcNow")]
    [InlineData("DateTime.Now")]
    [InlineData("DateTimeOffset.UtcNow")]
    [InlineData("DateTimeOffset.Now")]
    public void ProductionSource_DoesNotUseAmbientClock(string forbidden)
    {
        var offenders = Directory
            .EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains(forbidden, StringComparison.Ordinal))
            .ToArray();

        offenders.ShouldBeEmpty($"use TimeProvider instead of {forbidden}");
    }
}
