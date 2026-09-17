using Orchestrator.Domain.Budgets;

namespace Orchestrator.Application.Ports;

public sealed record TrustedInstructions(
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> OutOfScope);

public enum EvidenceSource
{
    RepositoryFile,
    SlackMessage,
    VaultNote,
    PullRequestComment,
    MeetingTranscript,
    DomainQuery,
    TaskEvidence,
}

public sealed record EvidenceItem(
    EvidenceSource Source,
    string Identifier,
    string Version,
    string Content);

public sealed record EvidenceBundle(
    IReadOnlyList<EvidenceItem> Items,
    IReadOnlyList<string> OmittedForBudget,
    IReadOnlyList<string> OmittedForAccess);

public sealed record ProviderPolicy(
    IReadOnlyList<string> AllowedProviders,
    bool FallbackAllowed);

public sealed record AgentRequest(
    Guid TaskId,
    string OperationId,
    AgentStage Stage,
    TrustedInstructions Instructions,
    EvidenceBundle Evidence,
    WorkspaceHandle Workspace,
    ProviderPolicy ProviderPolicy,
    BudgetLimits Budget,
    string AuthorizationRef);
