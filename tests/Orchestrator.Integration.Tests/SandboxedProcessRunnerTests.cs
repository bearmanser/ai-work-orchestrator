using System.Diagnostics;
using Orchestrator.Adapters.Agent;
using Shouldly;

namespace Orchestrator.Integration.Tests;

/// <summary>
/// Brings up the sandbox's supporting infrastructure once per test run: the
/// <c>orchestrator-agent</c> network and <c>egress-proxy</c> container from
/// deploy/docker-compose.yml, and the <c>orchestrator/worker:latest</c> image (built if missing).
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows dev/test override.</b> This machine has no <c>docker</c> CLI on the Windows PATH;
/// the daemon runs inside WSL2 and is reachable over <c>tcp://127.0.0.1:2375</c>. Production
/// (Linux, see deploy/docker-compose.yml) has a real <c>docker</c> CLI on PATH and uses
/// <see cref="SandboxOptions"/>'s default <c>["docker"]</c>, bound from <c>Sandbox:DockerCommand</c>
/// - see src/Orchestrator.Api/appsettings.Development.json for that environment's equivalent
/// override. This test file carries its own copy of the override (<see cref="DockerCommand"/>)
/// because these tests construct <see cref="SandboxedProcessRunner"/> directly rather than
/// through the Api's configuration/DI pipeline.
/// </para>
/// </remarks>
public sealed class SandboxInfrastructureFixture : IAsyncLifetime
{
    private const string WorkerImage = "orchestrator/worker:latest";

    // The docker CLI here runs inside WSL2 (see DockerCommand below), so any host path handed to
    // it - including -f's compose file path - must be translated to its WSL-mounted form; a raw
    // "C:\..." path is meaningless there. SandboxedProcessRunner.ToContainerHostPath does the same
    // translation for the workspace bind mount, for the same reason.
    private static readonly string ComposeFile = SandboxedProcessRunner.ToContainerHostPath(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "docker-compose.yml")));

    public static readonly IReadOnlyList<string> DockerCommand =
        ["wsl.exe", "-e", "env", "DOCKER_HOST=tcp://127.0.0.1:2375", "docker"];

    public SandboxOptions Options { get; } = new() { DockerCommand = DockerCommand };

    public async Task InitializeAsync()
    {
        var (upExit, _, upErr) = await RunDockerAsync(["compose", "-f", ComposeFile, "up", "-d", "egress-proxy"]);
        if (upExit != 0)
            throw new InvalidOperationException($"Failed to start egress-proxy via docker compose: {upErr}");

        var (imagesExit, imagesOut, _) = await RunDockerAsync(["images", "-q", WorkerImage]);
        if (imagesExit != 0 || string.IsNullOrWhiteSpace(imagesOut))
        {
            var (buildExit, _, buildErr) =
                await RunDockerAsync(["compose", "-f", ComposeFile, "build", "worker-image-builder"]);
            if (buildExit != 0)
                throw new InvalidOperationException($"Failed to build {WorkerImage}: {buildErr}");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static async Task<IReadOnlyList<string>> RunningContainerNamesAsync()
    {
        var (_, stdout, _) = await RunDockerAsync(["ps", "--format", "{{.Names}}"]);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerAsync(IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DockerCommand[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in DockerCommand.Skip(1).Concat(args))
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }
}

[CollectionDefinition(nameof(SandboxCollection))]
public sealed class SandboxCollection : ICollectionFixture<SandboxInfrastructureFixture>;

[Collection(nameof(SandboxCollection))]
[Trait("Category", "Integration")]
public sealed class SandboxedProcessRunnerTests(SandboxInfrastructureFixture fixture) : IDisposable
{
    private readonly List<string> _workspaces = [];
    private SandboxedProcessRunner Runner { get; } = new(fixture.Options, TimeProvider.System);

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
    }

    private string TempDirectory()
    {
        var path = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "sandbox-runner-tests-" + Guid.NewGuid().ToString("N"))).FullName;
        _workspaces.Add(path);
        return path;
    }

    private SandboxRequest Request(
        IReadOnlyList<string> command,
        string? workspace = null,
        IReadOnlyList<string>? allowedHosts = null,
        TimeSpan? timeout = null) =>
        new(
            Image: "orchestrator/worker:latest",
            Command: command,
            WorkspacePath: workspace ?? TempDirectory(),
            Environment: new Dictionary<string, string>(),
            AllowedHosts: allowedHosts ?? ["api.anthropic.com", "api.openai.com"],
            Timeout: timeout ?? TimeSpan.FromSeconds(30),
            StdinContent: null);

    [Fact]
    public async Task Run_SimpleCommand_ReturnsStdoutAndZeroExit()
    {
        var result = await Runner.RunAsync(Request(["sh", "-c", "echo hello"]), default);

        result.ExitCode.ShouldBe(0);
        result.Stdout.Trim().ShouldBe("hello");
    }

    [Fact]
    public async Task Run_WritesToWorkspace_ArePersistedOnTheHost()
    {
        var workspace = TempDirectory();
        await Runner.RunAsync(Request(["sh", "-c", "echo made-it > out.txt"], workspace), default);

        (await File.ReadAllTextAsync(Path.Combine(workspace, "out.txt"))).Trim().ShouldBe("made-it");
    }

    [Fact]
    public async Task Run_WriteOutsideWorkspace_Fails()
    {
        var result = await Runner.RunAsync(Request(["sh", "-c", "echo x > /etc/passwd"]), default);
        result.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task Run_RequestToDisallowedHost_Fails()
    {
        var result = await Runner.RunAsync(
            Request(["sh", "-c", "curl -sS -m 5 https://example.com"], allowedHosts: ["api.anthropic.com"]), default);

        result.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task Run_ExceedingTimeout_IsKilledAndReported()
    {
        var result = await Runner.RunAsync(
            Request(["sh", "-c", "sleep 30"], timeout: TimeSpan.FromSeconds(2)), default);

        result.TimedOut.ShouldBeTrue();
        result.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Run_CancellationRequested_KillsTheContainer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Should.ThrowAsync<OperationCanceledException>(
            () => Runner.RunAsync(Request(["sh", "-c", "sleep 60"], timeout: TimeSpan.FromMinutes(5)), cts.Token));

        // Give docker a moment to finish tearing the container down.
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await SandboxInfrastructureFixture.RunningContainerNamesAsync())
            .ShouldNotContain(n => n.StartsWith("orchestrator-agent-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_HostEnvironmentVariables_AreNotInherited()
    {
        Environment.SetEnvironmentVariable("ORCHESTRATOR_TEST_LEAK", "should-not-appear");
        var result = await Runner.RunAsync(Request(["sh", "-c", "env"]), default);
        result.Stdout.ShouldNotContain("should-not-appear");
    }

    [Fact]
    public async Task Run_EnormousOutput_IsTruncatedNotUnbounded()
    {
        var result = await Runner.RunAsync(
            Request(["sh", "-c", "yes abcdefghij | head -c 20000000"], timeout: TimeSpan.FromSeconds(60)), default);

        result.Stdout.Length.ShouldBeLessThanOrEqualTo(SandboxOptions.MaxCapturedOutputBytes + 200);
        result.Stdout.ShouldContain("[output truncated]");
    }
}
