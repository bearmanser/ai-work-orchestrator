using Orchestrator.Api.Configuration;
using Shouldly;

namespace Orchestrator.Integration.Tests;

public sealed class DotEnvLoaderTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dotenv-loader-tests-" + Guid.NewGuid())).FullName;

    [Fact]
    public void Find_NestedStartDirectory_FindsRepoRootEnvFile()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "ORCHESTRATOR_TEST_DOTENV_PROBE=found\n");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src", "Orchestrator.Api")).FullName;

        DotEnvLoader.FindDotEnvFile(nested).ShouldBe(Path.Combine(_root, ".env"));
    }

    [Fact]
    public void Find_NoEnvFile_ReturnsNull()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;

        DotEnvLoader.FindDotEnvFile(nested).ShouldBeNull();
    }

    [Fact]
    public void Load_EnvFile_PopulatesEnvironment()
    {
        const string name = "ORCHESTRATOR_TEST_DOTENV_POPULATE";
        var path = Path.Combine(_root, ".env");
        File.WriteAllText(path, name + "=from-dotenv-file\n");
        try
        {
            DotEnvLoader.Load(path);

            Environment.GetEnvironmentVariable(name).ShouldBe("from-dotenv-file");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Load_EnvFile_DoesNotOverwriteExistingVariables()
    {
        const string name = "ORCHESTRATOR_TEST_DOTENV_PRECEDENCE";
        var path = Path.Combine(_root, ".env");
        File.WriteAllText(path, name + "=from-dotenv-file\n");
        Environment.SetEnvironmentVariable(name, "from-process-env");
        try
        {
            DotEnvLoader.Load(path);

            Environment.GetEnvironmentVariable(name).ShouldBe("from-process-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
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
