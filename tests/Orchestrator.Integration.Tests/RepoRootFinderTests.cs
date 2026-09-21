using Orchestrator.Api.Configuration;
using Shouldly;

namespace Orchestrator.Integration.Tests;

public sealed class RepoRootFinderTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "reporoot-finder-tests-" + Guid.NewGuid())).FullName;

    [Fact]
    public void Find_NestedStartDirectory_FindsDirectoryContainingSolution()
    {
        File.WriteAllText(Path.Combine(_root, "Orchestrator.sln"), "// probe\n");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src", "Orchestrator.Api")).FullName;

        RepoRootFinder.Find(nested, Path.GetTempPath()).ShouldBe(_root);
    }

    [Fact]
    public void Find_NoSolutionFile_ReturnsFallback()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;
        var fallback = Path.Combine(_root, "fallback");

        RepoRootFinder.Find(nested, fallback).ShouldBe(Path.GetFullPath(fallback));
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
