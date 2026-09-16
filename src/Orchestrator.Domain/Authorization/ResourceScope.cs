namespace Orchestrator.Domain.Authorization;

/// <summary>
/// The pipeline stage an agent invocation is made for. Used to scope
/// <see cref="ActionKind.AgentInvoke"/> so an envelope minted for planning cannot be
/// replayed to authorize an implementation or review-stage call.
/// </summary>
public enum AgentStage
{
    Planning,
    Implementation,
    Review,
}

/// <summary>
/// The concrete thing an <see cref="ActionKind"/> is being attempted against. Each
/// case names exactly the fields <see cref="AuthorizationEnvelope.Covers"/> needs to
/// decide the request - nothing more - so a resource can never carry data that widens
/// what it is checked against.
/// </summary>
public abstract record ResourceScope
{
    private ResourceScope() { }

    public sealed record RepositoryResource(Guid RepositoryId, string Branch) : ResourceScope;

    public sealed record P1Resource(string Environment, string Company) : ResourceScope;

    public sealed record AgentResource(string Provider, AgentStage Stage) : ResourceScope;

    public sealed record VaultResource(string NotePath) : ResourceScope;

    public sealed record PersonResource(string PersonId) : ResourceScope;

    public sealed record ChannelResource(string ChannelId) : ResourceScope;

    public sealed record HostResource(string Host) : ResourceScope;

    public static ResourceScope Repository(Guid id, string branch) => new RepositoryResource(id, branch);

    public static ResourceScope P1(string environment, string company) => new P1Resource(environment, company);

    public static ResourceScope Agent(string provider, AgentStage stage) => new AgentResource(provider, stage);

    public static ResourceScope VaultNote(string path) => new VaultResource(path);

    public static ResourceScope Person(string id) => new PersonResource(id);

    public static ResourceScope Channel(string id) => new ChannelResource(id);

    public static ResourceScope NetworkHost(string host) => new HostResource(host);
}
