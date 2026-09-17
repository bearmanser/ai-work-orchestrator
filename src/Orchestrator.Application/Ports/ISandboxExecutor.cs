namespace Orchestrator.Application.Ports;

public sealed record SandboxExecutionRequest(
    string Image,
    IReadOnlyList<string> Command,
    string WorkspacePath,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> AllowedHosts,
    TimeSpan Timeout);

public sealed record SandboxExecutionResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut);

/// <summary>
/// Runs one sandboxed process. Implemented in the agent adapter on top of
/// <c>SandboxedProcessRunner</c>; kept as a port so Application never depends
/// on an adapter assembly.
/// </summary>
public interface ISandboxExecutor
{
    Task<SandboxExecutionResult> ExecuteAsync(SandboxExecutionRequest request, CancellationToken ct);
}
