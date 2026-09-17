namespace Orchestrator.Adapters.Agent;

/// <summary>
/// Configuration for <see cref="SandboxedProcessRunner"/>.
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>Hard cap on captured stdout/stderr per stream, to bound memory use.</summary>
    public const int MaxCapturedOutputBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The executable and leading arguments used to invoke Docker. Defaults to the plain
    /// <c>docker</c> CLI, which is what production (Linux, see deploy/docker-compose.yml) has on
    /// PATH.
    /// </summary>
    /// <remarks>
    /// This machine's Windows PATH has no <c>docker</c> CLI at all; the daemon runs inside WSL2
    /// and is reached over <c>tcp://127.0.0.1:2375</c>. Rather than hardcoding that assumption
    /// into the runner, this is a configuration value (bound from <c>Sandbox:DockerCommand</c>)
    /// so a Windows dev/test environment can override it to
    /// <c>["wsl.exe", "-e", "env", "DOCKER_HOST=tcp://127.0.0.1:2375", "docker"]</c> without
    /// touching the reviewable argument list the runner builds after this prefix.
    /// </remarks>
    public IReadOnlyList<string> DockerCommand { get; set; } = ["docker"];
}
