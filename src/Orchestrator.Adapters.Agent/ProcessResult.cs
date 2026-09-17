namespace Orchestrator.Adapters.Agent;

/// <summary>The outcome of running a command inside the sandbox.</summary>
/// <param name="ExitCode">
/// The container's process exit code. When <paramref name="TimedOut"/> is <c>true</c> this is a
/// sentinel value (-1) since the process was killed rather than allowed to exit naturally.
/// </param>
/// <param name="Stdout">Captured standard output, bounded to <see cref="SandboxOptions.MaxCapturedOutputBytes"/>.</param>
/// <param name="Stderr">Captured standard error, bounded to <see cref="SandboxOptions.MaxCapturedOutputBytes"/>.</param>
/// <param name="Duration">Wall-clock time the run took.</param>
/// <param name="TimedOut">Whether the run was killed for exceeding its requested timeout.</param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, TimeSpan Duration, bool TimedOut);
