namespace Orchestrator.Domain.Evidence;

/// <summary>
/// Where a check result came from. There is exactly one member on purpose: no
/// code path can construct a <see cref="CheckResult"/> that was not actually
/// executed here by the runtime, so an agent's self-reported outcome can never
/// be mistaken for verification.
/// </summary>
public enum CheckOrigin
{
    RuntimeExecuted,
}

/// <summary>A build or test command the runtime executed itself, with its log.</summary>
public sealed record CheckResult(
    string Command,
    bool Passed,
    int ExitCode,
    TimeSpan Duration,
    string LogArtifactId,
    CheckOrigin Origin);
