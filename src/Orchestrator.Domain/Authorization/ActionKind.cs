namespace Orchestrator.Domain.Authorization;

/// <summary>
/// The catalogue of actions an <see cref="AuthorizationEnvelope"/> can grant. Every
/// action a policy engine, approval flow or tool gateway checks must be one of these -
/// there is no free-text or reflective action name, so a new capability requires a new
/// member here and an explicit decision in <see cref="AuthorizationEnvelope.Covers"/>.
/// </summary>
public enum ActionKind
{
    RepositoryRead,
    RepositoryWrite,
    PullRequestCreate,
    PullRequestComment,
    DomainRead,
    DomainWrite,
    AgentInvoke,
    ContactPerson,
    PostMessage,
    MemoryRead,
    MemoryWrite,
    NetworkEgress,
}
