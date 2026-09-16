using Orchestrator.Domain.Identity;

namespace Orchestrator.Application.Identity;

public interface IOwnershipStore
{
    Task AddAsync(OwnershipEntry entry, CancellationToken ct);

    Task<IReadOnlyList<OwnershipEntry>> ListAsync(CancellationToken ct);

    Task<OwnershipEntry?> GetByIdAsync(Guid id, CancellationToken ct);

    Task RemoveAsync(Guid id, CancellationToken ct);
}

public sealed class InMemoryOwnershipStore : IOwnershipStore
{
    private readonly Dictionary<Guid, OwnershipEntry> entries = [];
    private readonly object gate = new();

    public Task AddAsync(OwnershipEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (gate)
        {
            entries[entry.Id] = entry;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OwnershipEntry>> ListAsync(CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<OwnershipEntry>>(entries.Values.ToList());
        }
    }

    public Task<OwnershipEntry?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        lock (gate)
        {
            entries.TryGetValue(id, out var entry);
            return Task.FromResult<OwnershipEntry?>(entry);
        }
    }

    public Task RemoveAsync(Guid id, CancellationToken ct)
    {
        lock (gate)
        {
            entries.Remove(id);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The ownership directory: who owns a repository or domain, and who may
/// confirm plans. A plan touching several repositories needs someone
/// authorized for every one of them (intersection, not union).
/// </summary>
public sealed class OwnershipDirectory(IPersonStore people, IOwnershipStore ownership)
{
    public async Task<IReadOnlyList<PersonIdentity>> FindOwnersAsync(
        OwnershipSubject subject, string subjectKey, CancellationToken ct)
    {
        var entries = await ownership.ListAsync(ct);
        var owners = new List<PersonIdentity>();
        foreach (var entry in entries)
        {
            if (entry.Subject != subject)
                continue;
            if (!string.Equals(entry.SubjectKey, subjectKey, StringComparison.Ordinal))
                continue;

            var person = await people.GetByIdAsync(entry.OwnerPersonId, ct);
            if (person is not null && person.IsActive)
                owners.Add(person);
        }

        return owners;
    }

    public async Task<IReadOnlyList<PersonIdentity>> FindPlanConfirmersAsync(
        IReadOnlyList<Guid> repositoryIds, CancellationToken ct)
    {
        if (repositoryIds.Count == 0)
            return [];

        var entries = await ownership.ListAsync(ct);
        HashSet<Guid>? eligible = null;

        foreach (var repositoryId in repositoryIds)
        {
            var key = repositoryId.ToString();
            var confirmers = new HashSet<Guid>();
            foreach (var entry in entries)
            {
                if (entry.Subject != OwnershipSubject.Repository)
                    continue;
                if (!string.Equals(entry.SubjectKey, key, StringComparison.Ordinal))
                    continue;
                if (!entry.CanConfirmPlans)
                    continue;

                var person = await people.GetByIdAsync(entry.OwnerPersonId, ct);
                if (person is not null && person.IsActive)
                    confirmers.Add(person.Id);
            }

            eligible = eligible is null ? confirmers : new HashSet<Guid>(eligible.Intersect(confirmers));
            if (eligible.Count == 0)
                return [];
        }

        if (eligible is null || eligible.Count == 0)
            return [];

        var result = new List<PersonIdentity>();
        foreach (var id in eligible)
        {
            var person = await people.GetByIdAsync(id, ct);
            if (person is not null && person.IsActive)
                result.Add(person);
        }

        return result;
    }
}
