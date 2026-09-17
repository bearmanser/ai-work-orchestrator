namespace Orchestrator.Application.Ports;

public enum AgentStatus
{
    Succeeded,
    Partial,
    Failed,
}

public sealed record ClaimedCheck(
    string Command,
    bool ClaimedPassed);

public sealed record AgentUsage(
    decimal CostUsd,
    TimeSpan Duration,
    int InputTokens = 0,
    int OutputTokens = 0);

public sealed record AgentResult(
    AgentStatus Status,
    string Provider,
    string Model,
    string BaselineCommit,
    string? ResultCommit,
    string? DiffArtifactId,
    IReadOnlyList<ClaimedCheck> ClaimedChecks,
    IReadOnlyList<string> UnresolvedQuestions,
    IReadOnlyList<string> Limitations,
    AgentUsage Usage,
    string? FailureDetail,
    string? OutputText = null);
