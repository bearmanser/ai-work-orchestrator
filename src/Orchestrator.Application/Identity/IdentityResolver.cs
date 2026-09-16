using Orchestrator.Domain.Identity;

namespace Orchestrator.Application.Identity;

public interface IPersonStore
{
    Task AddAsync(PersonIdentity person, CancellationToken ct);

    Task<PersonIdentity?> FindBySlackUserIdAsync(string slackUserId, CancellationToken ct);

    Task<PersonIdentity?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<PersonIdentity>> ListAsync(CancellationToken ct);

    Task RemoveAsync(Guid id, CancellationToken ct);
}

public sealed class InMemoryPersonStore : IPersonStore
{
    private readonly Dictionary<Guid, PersonIdentity> people = [];
    private readonly object gate = new();

    public Task AddAsync(PersonIdentity person, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        lock (gate)
        {
            people[person.Id] = person;
        }

        return Task.CompletedTask;
    }

    public Task<PersonIdentity?> FindBySlackUserIdAsync(string slackUserId, CancellationToken ct)
    {
        lock (gate)
        {
            var person = people.Values.FirstOrDefault(p =>
                string.Equals(p.SlackUserId, slackUserId, StringComparison.Ordinal));
            return Task.FromResult<PersonIdentity?>(person);
        }
    }

    public Task<PersonIdentity?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        lock (gate)
        {
            people.TryGetValue(id, out var person);
            return Task.FromResult<PersonIdentity?>(person);
        }
    }

    public Task<IReadOnlyList<PersonIdentity>> ListAsync(CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PersonIdentity>>(people.Values.ToList());
        }
    }

    public Task RemoveAsync(Guid id, CancellationToken ct)
    {
        lock (gate)
        {
            people.Remove(id);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Resolves a Slack user id to a mapped, active person. Returns null for an
/// unmapped or deactivated person; the caller must not invent one.
/// </summary>
public sealed class IdentityResolver(IPersonStore store)
{
    public async Task<PersonIdentity?> ResolveBySlackUserAsync(string slackUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slackUserId))
            return null;

        var person = await store.FindBySlackUserIdAsync(slackUserId, ct);
        if (person is null || !person.IsActive)
            return null;

        return person;
    }
}
