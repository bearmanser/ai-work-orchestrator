using Orchestrator.Application.Context;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Repositories;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Context;
using Orchestrator.Domain.Repositories;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class ContextAssemblerTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid BackendId = Guid.CreateVersion7();
    private static readonly Guid FrontendId = Guid.CreateVersion7();
    private static readonly Guid PayrollId = Guid.CreateVersion7();

    private static readonly ContextBudget Budget = new(100_000, 20, 5);

    [Fact]
    public async Task Assemble_IncludesFilesFromEveryRepositoryInTheEnvelope()
    {
        var harness = new Harness();
        var bundle = (await harness.Assembler.AssembleAsync(
            TaskId, harness.EnvelopeFor([BackendId, FrontendId]), Budget, default)).Bundle;

        bundle.Items.ShouldContain(i => i.Identifier.StartsWith("backend:"));
        bundle.Items.ShouldContain(i => i.Identifier.StartsWith("frontend:"));
    }

    [Fact]
    public async Task Assemble_ExcludesRepositoriesOutsideTheEnvelope_AndSaysSo()
    {
        var harness = new Harness();
        var bundle = (await harness.Assembler.AssembleAsync(
            TaskId, harness.EnvelopeFor([BackendId]), Budget, default)).Bundle;

        bundle.Items.ShouldNotContain(i => i.Identifier.StartsWith("payroll:"));
        bundle.OmittedForAccess.ShouldContain(o => o.Contains("payroll"));
    }

    [Fact]
    public async Task Assemble_OverBudget_OmitsAndRecordsWhatWasDropped()
    {
        var harness = new Harness();
        var bundle = (await harness.Assembler.AssembleAsync(TaskId, harness.EnvelopeFor([BackendId]),
            new ContextBudget(MaxCharacters: 2000, MaxFilesPerRepository: 3, MaxVaultNotes: 0), default)).Bundle;

        bundle.Items.Count.ShouldBeLessThanOrEqualTo(3);
        bundle.OmittedForBudget.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Assemble_PrioritisesInstructionFilesAndRelationshipEvidence()
    {
        var harness = new Harness();
        var bundle = (await harness.Assembler.AssembleAsync(TaskId, harness.EnvelopeFor([BackendId]),
            new ContextBudget(100_000, MaxFilesPerRepository: 2, 0), default)).Bundle;

        bundle.Items.Select(i => i.Identifier).ShouldContain(i => i.EndsWith("CLAUDE.md"));
    }

    [Fact]
    public async Task Assemble_RecordsASnapshotWithSourceVersionsAndOmissions()
    {
        var harness = new Harness();
        var before = DateTimeOffset.UtcNow;
        var (_, snapshotId) = await harness.Assembler.AssembleAsync(
            TaskId, harness.EnvelopeFor([BackendId]), Budget, default);
        var snapshot = await harness.LoadSnapshotAsync(snapshotId);

        snapshot.Sources.ShouldAllBe(s => !string.IsNullOrEmpty(s.Version));
        snapshot.RetrievedAt.ShouldBeInRange(before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Assemble_EveryItemIsUntrustedEvidence_NoneIsAnInstruction()
    {
        var harness = new Harness();
        var bundle = (await harness.Assembler.AssembleAsync(
            TaskId, harness.EnvelopeFor([BackendId]), Budget, default)).Bundle;

        bundle.Items.ShouldAllBe(i => Enum.IsDefined(i.Source));
        // EvidenceBundle has no channel by which an item becomes a TrustedInstruction.
        typeof(EvidenceBundle).GetProperties().ShouldNotContain(p => p.Name.Contains("Instruction"));
    }

    private sealed class Harness
    {
        private readonly FakeProfileStore _profiles = new();
        private readonly FakeRelationshipStore _relationships = new();
        private readonly FakeWorkspaceFiles _files = new();
        private readonly FakeSnapshotStore _snapshots = new();
        private readonly FakeArtifactStore _artifacts = new();

        public Harness()
        {
            var backend = Profile(BackendId, "backend", ["CLAUDE.md", "docs/guide.md"]);
            var frontend = Profile(FrontendId, "frontend", ["README.md"]);
            var payroll = Profile(PayrollId, "payroll", ["CLAUDE.md"]);
            _profiles.Seed(backend, frontend, payroll);

            _files.Seed(BackendId, new Dictionary<string, string>
            {
                ["CLAUDE.md"] = new string('c', 3000),
                ["docs/guide.md"] = new string('g', 3000),
            });
            _files.Seed(FrontendId, new Dictionary<string, string>
            {
                ["README.md"] = "frontend readme",
            });

            _relationships.Seed(new RepositoryRelationship(
                BackendId, PayrollId, RelationshipType.ConsumesHttpApi,
                [new RelationshipEvidence("src/Web/appsettings.json", "abc", "https://payroll.internal")],
                DateTimeOffset.UtcNow, "abc"));
        }

        public ContextAssembler Assembler => new(
            _profiles, _relationships, _files, vault: null, _snapshots, _artifacts,
            new FakeScopeStore(), TimeProvider.System);

        public AuthorizationEnvelope EnvelopeFor(IReadOnlyList<Guid> repositoryIds) => new(
            TaskId,
            "U-magnus",
            repositoryIds.Select(id => new RepositoryScope(id, ["*"], ["/"], RepositoryAccess.Read)).ToList(),
            ["staging"],
            [],
            ["replay"],
            [Domain.Authorization.AgentStage.Implementation],
            ["C1"],
            ["U-magnus"],
            [],
            [],
            new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3),
            DateTimeOffset.UtcNow.AddDays(7),
            DateTimeOffset.UtcNow);

        public async Task<SnapshotView> LoadSnapshotAsync(Guid id)
        {
            var snapshot = await _snapshots.GetAsync(id, default);
            snapshot.ShouldNotBeNull();
            return new SnapshotView(
                snapshot.Id,
                System.Text.Json.JsonSerializer.Deserialize<List<ContextSource>>(snapshot.SourcesJson) ?? [],
                snapshot.RetrievedAt);
        }

        private static RepositoryProfile Profile(Guid id, string name, IReadOnlyList<string> instructionFiles)
        {
            var profile = new RepositoryProfile(
                name, $"https://dev.azure.com/kameleon/Platform/_git/{name}", "main",
                "test", "U-owner", RepositoryAccess.Read,
                new BuildCommands(null, null, null, null, ".", TimeSpan.FromMinutes(5)),
                instructionFiles);
            profile.RecordInspection("abc", DateTimeOffset.UtcNow);
            typeof(RepositoryProfile).GetProperty(nameof(RepositoryProfile.Id))!.SetValue(profile, id);
            return profile;
        }
    }

    private sealed record SnapshotView(Guid Id, List<ContextSource> Sources, DateTimeOffset RetrievedAt);

    private sealed class FakeProfileStore : IRepositoryProfileStore
    {
        private readonly Dictionary<Guid, RepositoryProfile> _profiles = [];

        public void Seed(params RepositoryProfile[] profiles)
        {
            foreach (var profile in profiles)
                _profiles[profile.Id] = profile;
        }

        public Task CreateAsync(RepositoryProfile profile, CancellationToken ct)
        {
            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }

        public Task<RepositoryProfile?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            _profiles.TryGetValue(id, out var profile);
            return Task.FromResult(profile);
        }

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepositoryProfile>>(_profiles.Values.ToList());

        public Task<bool> RemoteExistsAsync(string normalizedRemoteUrl, CancellationToken ct) =>
            Task.FromResult(_profiles.Values.Any(p => p.RemoteUrl == normalizedRemoteUrl));

        public Task UpdateAsync(RepositoryProfile profile, uint expectedVersion, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeRelationshipStore : IRelationshipStore
    {
        private readonly List<RepositoryRelationship> _relationships = [];

        public void Seed(params RepositoryRelationship[] relationships) => _relationships.AddRange(relationships);

        public Task AddAsync(RepositoryRelationship relationship, CancellationToken ct)
        {
            _relationships.Add(relationship);
            return Task.CompletedTask;
        }

        public Task<RepositoryRelationship?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_relationships.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<RepositoryRelationship>> ListForRepositoryAsync(Guid repositoryId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RepositoryRelationship>>(_relationships
                .Where(r => r.SourceRepositoryId == repositoryId || r.TargetRepositoryId == repositoryId)
                .ToList());

        public Task RemoveAsync(RepositoryRelationship relationship, CancellationToken ct)
        {
            _relationships.Remove(relationship);
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeWorkspaceFiles : IWorkspaceFileReader
    {
        private readonly Dictionary<Guid, Dictionary<string, string>> _trees = [];

        public void Seed(Guid repositoryId, Dictionary<string, string> files) => _trees[repositoryId] = files;

        public Task<IReadOnlyList<string>> ListFilesAsync(Guid repositoryId, string baseBranch, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                _trees.TryGetValue(repositoryId, out var tree) ? tree.Keys.ToList() : []);

        public Task<string?> ReadFileAsync(Guid repositoryId, string baseBranch, string path, CancellationToken ct) =>
            Task.FromResult<string?>(_trees.TryGetValue(repositoryId, out var tree)
                && tree.TryGetValue(path, out var content) ? content : null);
    }

    private sealed class FakeSnapshotStore : IContextSnapshotStore
    {
        private readonly Dictionary<Guid, ContextSnapshot> _snapshots = [];

        public Task AddAsync(ContextSnapshot snapshot, CancellationToken ct)
        {
            _snapshots[snapshot.Id] = snapshot;
            return Task.CompletedTask;
        }

        public Task<ContextSnapshot?> GetAsync(Guid snapshotId, CancellationToken ct)
        {
            _snapshots.TryGetValue(snapshotId, out var snapshot);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        public Task<string> PutAsync(ArtifactDescriptor descriptor, Stream content, CancellationToken ct) =>
            Task.FromResult(Guid.CreateVersion7().ToString("N"));

        public Task<Stream> OpenAsync(string artifactId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeScopeStore : IPlanningScopeStore
    {
        public Task<WorkTask> GetTaskAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(new WorkTask("objective", "U-magnus", TaskSource.Slack, $"t-{id}", DateTimeOffset.UtcNow));

        public Task SaveTaskAsync(WorkTask task, CancellationToken ct) => Task.CompletedTask;

        public Task<AuthorizationEnvelope?> GetEnvelopeAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<AuthorizationEnvelope?>(null);

        public Task<BudgetLedger?> GetLedgerAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<BudgetLedger?>(null);

        public Task SaveLedgerAsync(BudgetLedger ledger, CancellationToken ct)
        {
            throw new NotImplementedException();
        }
    }
}
