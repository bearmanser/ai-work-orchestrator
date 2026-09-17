namespace Orchestrator.Domain.Evidence;

/// <summary>A diff summary without the full text (which lives in an artifact).</summary>
public sealed record GitDiffSummary(
    string BaselineCommit,
    string HeadCommit,
    IReadOnlyList<string> ChangedFiles,
    string? DiffArtifactId);

/// <summary>Everything that proves what a task did and what remains open.</summary>
public sealed record TaskEvidence(
    IReadOnlyList<CheckResult> RuntimeChecks,
    IReadOnlyList<DomainCheckResult> DomainChecks,
    GitDiffSummary Diff,
    string PlanRevisionHash,
    IReadOnlyList<string> UnresolvedQuestions);
