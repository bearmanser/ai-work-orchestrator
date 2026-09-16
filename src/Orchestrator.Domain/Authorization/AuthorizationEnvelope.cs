using Orchestrator.Domain.Budgets;

namespace Orchestrator.Domain.Authorization;

/// <summary>Read or write access an <see cref="AuthorizationEnvelope"/> grants over a repository.</summary>
public enum RepositoryAccess
{
    Read,
    Write,
}

/// <summary>
/// One repository an envelope reaches into: which branches, which path prefixes, and
/// whether that reach is read-only or read-write.
/// </summary>
public sealed record RepositoryScope(
    Guid RepositoryId,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> PathPrefixes,
    RepositoryAccess Access);

/// <summary>
/// The machine-enforced boundary of a whole task. Every side effect the agent loop can
/// have on the outside world - touching a repository, reading or writing a P1 domain,
/// invoking an agent provider at a given pipeline stage, messaging a person or a Slack
/// channel, reading or writing a vault note, reaching a network host - is checked
/// against exactly one envelope via <see cref="Covers"/>.
///
/// The envelope is immutable after creation: there is no setter, no "Add..." method,
/// and no with-style copy helper, because any of those would be a way to widen an
/// existing grant. The only mutator is <see cref="Revoke"/>, and it only ever narrows.
/// A task that needs a wider grant gets a brand new envelope, minted by an approval -
/// it never gets this one enlarged.
/// </summary>
public sealed class AuthorizationEnvelope
{
    private AuthorizationEnvelope() { } // EF

    public AuthorizationEnvelope(
        Guid taskId,
        string requesterId,
        IReadOnlyList<RepositoryScope> repositories,
        IReadOnlyList<string> p1Environments,
        IReadOnlyList<string> p1Companies,
        IReadOnlyList<string> allowedProviders,
        IReadOnlyList<AgentStage> allowedStages,
        IReadOnlyList<string> slackChannels,
        IReadOnlyList<string> contactablePeople,
        IReadOnlyList<string> vaultPathPrefixes,
        IReadOnlyList<string> networkDestinations,
        BudgetLimits budget,
        DateTimeOffset expiresAt,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(requesterId))
            throw new ArgumentException("Requester is required", nameof(requesterId));
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(p1Environments);
        ArgumentNullException.ThrowIfNull(p1Companies);
        ArgumentNullException.ThrowIfNull(allowedProviders);
        ArgumentNullException.ThrowIfNull(allowedStages);
        ArgumentNullException.ThrowIfNull(slackChannels);
        ArgumentNullException.ThrowIfNull(contactablePeople);
        ArgumentNullException.ThrowIfNull(vaultPathPrefixes);
        ArgumentNullException.ThrowIfNull(networkDestinations);
        ArgumentNullException.ThrowIfNull(budget);
        if (expiresAt <= createdAt)
            throw new ArgumentOutOfRangeException(nameof(expiresAt), expiresAt, "Expiry must be after creation.");

        Id = Guid.CreateVersion7();
        TaskId = taskId;
        RequesterId = requesterId;
        Repositories = repositories;
        P1Environments = p1Environments;
        P1Companies = p1Companies;
        AllowedProviders = allowedProviders;
        AllowedStages = allowedStages;
        SlackChannels = slackChannels;
        ContactablePeople = contactablePeople;
        VaultPathPrefixes = vaultPathPrefixes;
        NetworkDestinations = networkDestinations;
        Budget = budget;
        ExpiresAt = expiresAt.ToUniversalTime();
        RevokedAt = null;
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public string RequesterId { get; private set; } = null!;
    public IReadOnlyList<RepositoryScope> Repositories { get; private set; } = [];
    public IReadOnlyList<string> P1Environments { get; private set; } = [];
    public IReadOnlyList<string> P1Companies { get; private set; } = [];
    public IReadOnlyList<string> AllowedProviders { get; private set; } = [];
    public IReadOnlyList<AgentStage> AllowedStages { get; private set; } = [];
    public IReadOnlyList<string> SlackChannels { get; private set; } = [];
    public IReadOnlyList<string> ContactablePeople { get; private set; } = [];
    public IReadOnlyList<string> VaultPathPrefixes { get; private set; } = [];
    public IReadOnlyList<string> NetworkDestinations { get; private set; } = [];
    public BudgetLimits Budget { get; private set; } = null!;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }

    /// <summary>
    /// Revokes the envelope as of <paramref name="at"/>. Revocation only ever narrows:
    /// calling this a second time can only pull the effective revocation moment
    /// earlier, never push it later or clear it, so an already-revoked envelope can
    /// never be un-revoked or have its dead-line postponed.
    /// </summary>
    public void Revoke(DateTimeOffset at, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A revocation reason is required", nameof(reason));

        var utcAt = at.ToUniversalTime();
        if (RevokedAt is null || utcAt < RevokedAt)
            RevokedAt = utcAt;
        RevocationReason = reason;
    }

    /// <summary>
    /// Whether this envelope currently grants <paramref name="action"/> against
    /// <paramref name="resource"/> at <paramref name="now"/>. Expiry and revocation are
    /// both checked against the caller-supplied clock, never a value captured at
    /// construction. Every combination of action and resource that this switch does
    /// not explicitly recognise falls through to the default arm and is denied - a new
    /// <see cref="ActionKind"/> added later is refused until someone deliberately wires
    /// it up here, not silently admitted.
    /// </summary>
    public bool Covers(ActionKind action, ResourceScope resource, DateTimeOffset now)
    {
        if (now >= ExpiresAt)
            return false;
        if (RevokedAt is not null && now >= RevokedAt.Value)
            return false;

        return (action, resource) switch
        {
            (ActionKind.RepositoryRead, ResourceScope.RepositoryResource r) => CoversRepository(r, RepositoryAccess.Read),
            (ActionKind.RepositoryWrite, ResourceScope.RepositoryResource r) => CoversRepository(r, RepositoryAccess.Write),
            (ActionKind.PullRequestCreate, ResourceScope.RepositoryResource r) => CoversRepository(r, RepositoryAccess.Write),
            (ActionKind.PullRequestComment, ResourceScope.RepositoryResource r) => CoversRepository(r, RepositoryAccess.Read),
            (ActionKind.DomainRead, ResourceScope.P1Resource p) => CoversP1(p),
            (ActionKind.DomainWrite, ResourceScope.P1Resource p) => CoversP1(p),
            (ActionKind.AgentInvoke, ResourceScope.AgentResource a) => CoversAgent(a),
            (ActionKind.ContactPerson, ResourceScope.PersonResource person) => ContactablePeople.Contains(person.PersonId),
            (ActionKind.PostMessage, ResourceScope.ChannelResource c) => SlackChannels.Contains(c.ChannelId),
            (ActionKind.MemoryRead, ResourceScope.VaultResource v) => CoversVaultPath(v),
            (ActionKind.MemoryWrite, ResourceScope.VaultResource v) => CoversVaultPath(v),
            (ActionKind.NetworkEgress, ResourceScope.HostResource h) =>
                NetworkDestinations.Contains(h.Host, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private bool CoversRepository(ResourceScope.RepositoryResource resource, RepositoryAccess required)
    {
        foreach (var scope in Repositories)
        {
            if (scope.RepositoryId != resource.RepositoryId)
                continue;
            if (scope.Access < required)
                continue;
            if (BranchMatches(scope.Branches, resource.Branch))
                return true;
        }

        return false;
    }

    private static bool BranchMatches(IReadOnlyList<string> patterns, string branch)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.EndsWith('*'))
            {
                if (branch.StartsWith(pattern[..^1], StringComparison.Ordinal))
                    return true;
            }
            else if (branch == pattern)
            {
                return true;
            }
        }

        return false;
    }

    private bool CoversP1(ResourceScope.P1Resource resource) =>
        P1Environments.Contains(resource.Environment) && P1Companies.Contains(resource.Company);

    private bool CoversAgent(ResourceScope.AgentResource resource) =>
        AllowedProviders.Contains(resource.Provider) && AllowedStages.Contains(resource.Stage);

    private bool CoversVaultPath(ResourceScope.VaultResource resource)
    {
        var normalized = resource.NotePath.Replace('\\', '/');
        foreach (var prefix in VaultPathPrefixes)
        {
            var normalizedPrefix = prefix.Replace('\\', '/');
            if (!normalizedPrefix.EndsWith('/'))
                normalizedPrefix += "/";
            if (normalized.StartsWith(normalizedPrefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
