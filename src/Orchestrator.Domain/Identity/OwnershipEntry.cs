namespace Orchestrator.Domain.Identity;

public enum OwnershipSubject
{
    Repository,
    Domain,
}

/// <summary>
/// One ownership grant: a person owns a repository or domain, optionally with
/// the right to confirm plans or approve restricted actions.
/// </summary>
public sealed record OwnershipEntry(
    Guid Id,
    OwnershipSubject Subject,
    string SubjectKey,
    Guid OwnerPersonId,
    bool CanConfirmPlans,
    bool CanApproveRestrictedActions);
