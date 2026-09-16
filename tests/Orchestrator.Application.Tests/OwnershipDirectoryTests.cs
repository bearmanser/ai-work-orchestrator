using Orchestrator.Application.Identity;
using Orchestrator.Domain.Identity;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class OwnershipDirectoryTests
{
    private static readonly Guid MagnusId = Guid.CreateVersion7();
    private static readonly Guid ReadOnlyOwnerId = Guid.CreateVersion7();
    private static readonly Guid FrontendDevId = Guid.CreateVersion7();
    private static readonly Guid BackendRepoId = Guid.CreateVersion7();
    private static readonly Guid FrontendRepoId = Guid.CreateVersion7();

    private readonly OwnershipDirectory Directory;

    public OwnershipDirectoryTests()
    {
        var people = new InMemoryPersonStore();
        people.AddAsync(new PersonIdentity(MagnusId, "Magnus", "U-magnus", "magnus-ado", "magnus@example.com", true), default).GetAwaiter().GetResult();
        people.AddAsync(new PersonIdentity(ReadOnlyOwnerId, "Read Only", "U-readonly", "readonly-ado", "readonly@example.com", true), default).GetAwaiter().GetResult();
        people.AddAsync(new PersonIdentity(FrontendDevId, "Frontend Dev", "U-frontend", "frontend-ado", "frontend@example.com", true), default).GetAwaiter().GetResult();

        var ownership = new InMemoryOwnershipStore();
        ownership.AddAsync(new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository, BackendRepoId.ToString(), MagnusId, true, false), default).GetAwaiter().GetResult();
        ownership.AddAsync(new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository, BackendRepoId.ToString(), ReadOnlyOwnerId, false, false), default).GetAwaiter().GetResult();
        ownership.AddAsync(new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository, FrontendRepoId.ToString(), MagnusId, true, false), default).GetAwaiter().GetResult();
        ownership.AddAsync(new OwnershipEntry(Guid.CreateVersion7(), OwnershipSubject.Repository, FrontendRepoId.ToString(), FrontendDevId, true, false), default).GetAwaiter().GetResult();

        Directory = new OwnershipDirectory(people, ownership);
    }

    [Fact]
    public async Task FindOwners_RepositoryWithTwoOwners_ReturnsBoth() =>
        (await Directory.FindOwnersAsync(OwnershipSubject.Repository, BackendRepoId.ToString(), default))
            .Count.ShouldBe(2);

    [Fact]
    public async Task FindOwners_UnknownSubject_ReturnsEmpty_NotAFallbackOwner() =>
        (await Directory.FindOwnersAsync(OwnershipSubject.Domain, "unheard-of", default)).ShouldBeEmpty();

    [Fact]
    public async Task FindPlanConfirmers_OnlyReturnsPeopleWithThatRight()
    {
        var confirmers = await Directory.FindPlanConfirmersAsync([BackendRepoId], default);
        confirmers.ShouldAllBe(p => p.Id != ReadOnlyOwnerId);
    }

    [Fact]
    public async Task FindPlanConfirmers_TaskSpanningTwoRepositories_ReturnsTheIntersectionRule()
    {
        // A person may confirm only if they can confirm for every repository the plan changes.
        var confirmers = await Directory.FindPlanConfirmersAsync([BackendRepoId, FrontendRepoId], default);
        confirmers.Select(p => p.Id).ShouldBe([MagnusId]);
    }
}
