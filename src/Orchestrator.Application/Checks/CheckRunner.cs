using System.Text;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Repositories;

namespace Orchestrator.Application.Checks;

/// <summary>Configuration for <see cref="CheckRunner"/>.</summary>
public sealed class CheckRunnerOptions
{
    public string Image { get; set; } = "orchestrator/worker:latest";
}

/// <summary>
/// Executes the build and test commands stored on a <see cref="RepositoryProfile"/>
/// — and only those — inside the sandbox with no network access, storing each
/// run's combined output as an artifact. A non-zero exit stops the sequence;
/// later commands are neither run nor reported. Agent-reported outcomes never
/// enter here; compare them afterwards with <see cref="CheckClaimComparer"/>.
/// </summary>
public sealed class CheckRunner(
    ISandboxExecutor executor,
    IArtifactStore artifacts,
    CheckRunnerOptions options)
{
    /// <summary>
    /// Environment every check runs with. The sandbox image carries a .NET SDK
    /// whose CLI refuses to start without these: first-run setup must be skipped
    /// (the filesystem is read-only outside the workspace) and HOME/TMPDIR must
    /// point at the writable tmpfs. Harmless for non-dotnet commands.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> SandboxDotNetEnvironment =
        new Dictionary<string, string>
        {
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["HOME"] = "/tmp",
            ["TMPDIR"] = "/tmp",
            ["TEMP"] = "/tmp",
            ["TMP"] = "/tmp",
        };

    public async Task<IReadOnlyList<CheckResult>> RunAsync(
        Guid taskId, RepositoryProfile profile, string workspacePath, CancellationToken ct)
    {
        var commands = new (string Name, string? Command)[]
        {
            ("restore", profile.BuildCommands.Restore),
            ("build", profile.BuildCommands.Build),
            ("test", profile.BuildCommands.Test),
            ("lint", profile.BuildCommands.Lint),
        }
        .Where(c => !string.IsNullOrWhiteSpace(c.Command))
        .ToList();

        var results = new List<CheckResult>();
        foreach (var (_, command) in commands)
        {
            var result = await RunOneAsync(taskId, command!, workspacePath, profile.BuildCommands.Timeout, ct);
            results.Add(result);
            if (!result.Passed)
                break;
        }

        return results;
    }

    private async Task<CheckResult> RunOneAsync(
        Guid taskId, string command, string workspacePath, TimeSpan timeout, CancellationToken ct)
    {
        var execution = await executor.ExecuteAsync(new SandboxExecutionRequest(
            options.Image,
            ["sh", "-c", command],
            workspacePath,
            SandboxDotNetEnvironment,
            AllowedHosts: [],
            timeout), ct);

        var output = string.IsNullOrEmpty(execution.Stderr)
            ? execution.Stdout
            : execution.Stdout + "\n[stderr]\n" + execution.Stderr;

        var artifactId = await artifacts.PutAsync(
            new ArtifactDescriptor(taskId, "check-log", "text/plain", ArtifactSensitivity.Internal, "check-runner"),
            new MemoryStream(Encoding.UTF8.GetBytes(output)), ct);

        return new CheckResult(
            command,
            Passed: execution.ExitCode == 0 && !execution.TimedOut,
            execution.ExitCode,
            execution.Duration,
            artifactId,
            CheckOrigin.RuntimeExecuted);
    }
}
