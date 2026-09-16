using System.Collections.Immutable;
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
    RepositoryAccess Access)
{
    /// <summary>
    /// Returns a copy with <see cref="Branches"/> and <see cref="PathPrefixes"/> backed
    /// by an immutable collection, so a caller cannot reach past the envelope's own
    /// defensive copy by mutating the lists nested one level inside a
    /// <see cref="RepositoryScope"/>. Used by both the constructor and the EF
    /// deserialization path, since System.Text.Json materialises an
    /// <see cref="IReadOnlyList{T}"/>-typed property as a plain mutable
    /// <see cref="List{T}"/>.
    /// </summary>
    public RepositoryScope AsImmutable() => this with
    {
        Branches = Branches.ToImmutableList(),
        PathPrefixes = PathPrefixes.ToImmutableList(),
    };
}

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
        // Defensive copies into an immutable collection type at the one entry point a
        // caller controls: IReadOnlyList<T> is only a read-only *view* of whatever list
        // was passed in, so without this a caller holding the original List<T>
        // reference (or one who casts the property back to List<T>) could mutate the
        // envelope in place after construction - the exact widening path Revoke is
        // supposed to be the only way to reach. Each RepositoryScope's own Branches and
        // PathPrefixes get the same treatment so the loophole can't be reached one
        // level down either.
        Repositories = repositories.Select(r => r.AsImmutable()).ToImmutableList();
        P1Environments = p1Environments.ToImmutableList();
        P1Companies = p1Companies.ToImmutableList();
        AllowedProviders = allowedProviders.ToImmutableList();
        AllowedStages = allowedStages.ToImmutableList();
        SlackChannels = slackChannels.ToImmutableList();
        ContactablePeople = contactablePeople.ToImmutableList();
        VaultPathPrefixes = vaultPathPrefixes.ToImmutableList();
        NetworkDestinations = networkDestinations.ToImmutableList();
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
            // A PR comment mutates remote state - it's visible to reviewers, can
            // trigger notifications and automation, and is itself task input that
            // later feeds back into the workflow - so it requires Write, same as
            // RepositoryWrite and PullRequestCreate. Of the four repository-resource
            // actions, RepositoryRead is the only one that is genuinely read-only.
            (ActionKind.PullRequestComment, ResourceScope.RepositoryResource r) => CoversRepository(r, RepositoryAccess.Write),
            // DomainRead/DomainWrite: the brief's field list gives P1 exactly one axis
            // of scope - P1Environments x P1Companies - with no separate read/write
            // grant, unlike RepositoryScope's explicit RepositoryAccess. DomainWrite
            // does change state outside this process (it writes to a P1 domain), but
            // there is no narrower list this envelope type could check it against
            // without inventing a field the brief does not define; membership in the
            // same environment/company scope is the only - and therefore the most
            // restrictive available - gate for both.
            (ActionKind.DomainRead, ResourceScope.P1Resource p) => CoversP1(p),
            (ActionKind.DomainWrite, ResourceScope.P1Resource p) => CoversP1(p),
            (ActionKind.AgentInvoke, ResourceScope.AgentResource a) => CoversAgent(a),
            // ContactPerson and PostMessage both change state outside this process (a
            // person is messaged, a channel receives a post) but the brief models each
            // as a flat allow-list (ContactablePeople, SlackChannels) with no separate
            // read variant to compare against - list membership is already the
            // strictest available check.
            (ActionKind.ContactPerson, ResourceScope.PersonResource person) => ContactablePeople.Contains(person.PersonId),
            (ActionKind.PostMessage, ResourceScope.ChannelResource c) => SlackChannels.Contains(c.ChannelId),
            // MemoryRead/MemoryWrite: same shape as the P1 case - VaultPathPrefixes is
            // a single list with no separate write-scope field, so a MemoryWrite is
            // gated by the same prefix membership as MemoryRead; that is the strictest
            // check the brief's field list allows without inventing a new field.
            (ActionKind.MemoryRead, ResourceScope.VaultResource v) => CoversVaultPath(v),
            (ActionKind.MemoryWrite, ResourceScope.VaultResource v) => CoversVaultPath(v),
            // NetworkEgress changes state outside this process (a network call is
            // made) and is gated by NetworkDestinations, the only list the brief gives
            // this resource kind.
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
