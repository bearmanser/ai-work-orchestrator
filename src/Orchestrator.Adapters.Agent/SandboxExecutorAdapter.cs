using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Agent;

/// <summary>
/// <see cref="ISandboxExecutor"/> implemented by the sandboxed process runner.
/// </summary>
public sealed class SandboxExecutorAdapter(SandboxedProcessRunner runner) : ISandboxExecutor
{
    public async Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request, CancellationToken ct)
    {
        var result = await runner.RunAsync(new SandboxRequest(
            request.Image,
            request.Command,
            request.WorkspacePath,
            request.Environment,
            request.AllowedHosts,
            request.Timeout,
            StdinContent: null), ct);

        return new SandboxExecutionResult(
            result.ExitCode, result.Stdout, result.Stderr, result.Duration, result.TimedOut);
    }
}
