using System.Diagnostics;
using Orchestrator.Adapters.Agent;
using Orchestrator.Application.Checks;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Repositories;
using Orchestrator.Infrastructure.Artifacts;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(SandboxCollection))]
[Trait("Category", "Integration")]
public sealed class CheckRunnerTests(SandboxInfrastructureFixture sandbox) : IAsyncLifetime, IDisposable
{
    private static readonly Guid TaskId = Guid.CreateVersion7();

    // Postgres is managed manually (rather than through PostgresCollection) so these
    // tests share SandboxCollection's serialization: container-name assertions in the
    // sandbox tests assume no other orchestrator-agent-* container is running.
    private readonly PostgresFixture _postgres = new();
    private readonly List<string> _workspaces = [];
    private readonly string _artifactRoot =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "check-runner-artifacts-" + Guid.NewGuid())).FullName;

    public async Task InitializeAsync() => await _postgres.InitializeAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    public void Dispose()
    {
        foreach (var workspace in _workspaces)
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a stray container touching a file a beat late shouldn't fail the test.
            }
        }

        if (Directory.Exists(_artifactRoot))
            Directory.Delete(_artifactRoot, recursive: true);
    }

    [Fact]
    public async Task Run_PassingTestSuite_ReportsPassedWithALogArtifact()
    {
        var workspace = await ScratchDotNetProjectAsync(testPasses: true);
        var results = await Runner().RunAsync(TaskId, ProfileWith("dotnet test"), workspace, default);

        results.Single().Passed.ShouldBeTrue();
        results.Single().LogArtifactId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Run_FailingTestSuite_ReportsFailed_AndTheLogContainsTheFailure()
    {
        var workspace = await ScratchDotNetProjectAsync(testPasses: false);
        var results = await Runner().RunAsync(TaskId, ProfileWith("dotnet test"), workspace, default);

        results.Single().Passed.ShouldBeFalse();
        (await ReadArtifactAsync(results.Single().LogArtifactId)).ShouldContain("Failed!");
    }

    [Fact]
    public async Task Run_CommandNotOnTheProfile_IsNeverExecuted()
    {
        // A command discovered in the repository but not saved on the profile must not run.
        var workspace = await ScratchDotNetProjectAsync(testPasses: true);
        var results = await Runner().RunAsync(TaskId, ProfileWith(build: null, test: null), workspace, default);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_CommandExceedingProfileTimeout_IsKilledAndReportedFailed()
    {
        var workspace = await ScratchDotNetProjectAsync(testPasses: true);
        var profile = ProfileWith("sleep 120", timeout: TimeSpan.FromSeconds(3));

        var result = (await Runner().RunAsync(TaskId, profile, workspace, default)).Single();

        result.Passed.ShouldBeFalse();
        result.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void CheckResult_CannotBeConstructedWithANonRuntimeOrigin() =>
        Enum.GetValues<CheckOrigin>().ShouldBe([CheckOrigin.RuntimeExecuted]);

    private CheckRunner Runner() => new(
        new SandboxExecutorAdapter(new SandboxedProcessRunner(sandbox.Options, TimeProvider.System)),
        new FileSystemArtifactStore(_artifactRoot, _postgres.NewDbContext(), TimeProvider.System),
        new CheckRunnerOptions { Image = "orchestrator/worker:latest" });

    private RepositoryProfile ProfileWith(
        string? test = null,
        string? restore = null,
        string? build = null,
        string? lint = null,
        TimeSpan? timeout = null) => new(
        $"check-{Guid.NewGuid():N}",
        $"https://dev.azure.com/kameleon/Platform/_git/check-{Guid.NewGuid():N}",
        "main",
        "check runner test",
        "U-owner",
        RepositoryAccess.Read,
        new BuildCommands(restore, build, test, lint, ".", timeout ?? TimeSpan.FromMinutes(5)),
        []);

    /// <summary>
    /// A minimal xUnit project that restores and tests fully offline inside the
    /// sandbox: packages are restored on the host into a workspace-local
    /// `.nuget` folder (see nuget.config) and build intermediates are redirected
    /// to tmpfs (Windows bind mounts reject the timestamp updates MSBuild needs).
    /// </summary>
    private async Task<string> ScratchDotNetProjectAsync(bool testPasses)
    {
        var workspace = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "check-runner-ws-" + Guid.NewGuid().ToString("N"))).FullName;
        _workspaces.Add(workspace);

        await File.WriteAllTextAsync(Path.Combine(workspace, "scratch.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsPackable>false</IsPackable>
                <BaseIntermediateOutputPath>/tmp/check-obj/</BaseIntermediateOutputPath>
                <BaseOutputPath>/tmp/check-bin/</BaseOutputPath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
                <PackageReference Include="xunit" Version="2.9.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value=".nuget" /></config>
              <packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
            </configuration>
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace, "UnitTest1.cs"), testPasses
            ? "using Xunit;\npublic sealed class UnitTest1 { [Fact] public void Passes() => Assert.True(true); }\n"
            : "using Xunit;\npublic sealed class UnitTest1 { [Fact] public void Fails() => Assert.True(false); }\n");

        var restore = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "restore",
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        await restore.WaitForExitAsync();
        if (restore.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Host restore of the scratch project failed: {await restore.StandardError.ReadToEndAsync()}");
        }

        return workspace;
    }

    private async Task<string> ReadArtifactAsync(string artifactId)
    {
        await using var stream = await new FileSystemArtifactStore(
            _artifactRoot, _postgres.NewDbContext(), TimeProvider.System).OpenAsync(artifactId, default);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
