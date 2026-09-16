namespace Orchestrator.Domain.Repositories;

/// <summary>
/// How to restore, build, test, and lint a repository checkout. Executed by the
/// orchestrator runtime (Task 29), never by the agent.
/// </summary>
public sealed record BuildCommands(
    string? Restore,
    string? Build,
    string? Test,
    string? Lint,
    string WorkingDirectory,
    TimeSpan Timeout);
