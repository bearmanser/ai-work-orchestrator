using System.Text.Json;
using Orchestrator.Application.Agents;
using Orchestrator.Application.Evidence;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Memory;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Identity;
using Orchestrator.Domain.Memory;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class MemoryCuratorTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();

    private static readonly PersonIdentity Magnus = new(
        Guid.CreateVersion7(), "Magnus", "U-magnus", null, "magnus@example.com", true);

    [Fact]
    public async Task Curate_DurableLesson_IsPublishedWithoutHumanApproval()
    {
        var harness = new Harness();
        harness.Agent.Proposes([DurableCandidate()]);

        var result = await harness.Curator.CurateAsync(TaskId, default);

        result.Published.Count.ShouldBe(1);
        harness.Vault.Writes.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Curate_RoutineLog_PublishesNothing()
    {
        var harness = new Harness();
        harness.Agent.Proposes([Candidate("Ran dotnet test and it passed")]);

        var result = await harness.Curator.CurateAsync(TaskId, default);

        result.Published.ShouldBeEmpty();
        harness.Vault.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Curate_DuplicateOfAnExistingNote_UpdatesItRatherThanAddingAnother()
    {
        var harness = new Harness();
        await harness.Vault.SeedAsync("Projects/Orchestrator/Decisions.md", "Picked orders are exposed read-only");
        harness.Agent.Proposes([DurableCandidate("Picked orders are exposed read-only because ...")]);

        await harness.Curator.CurateAsync(TaskId, default);

        harness.Vault.Writes.Single().Path.ShouldBe("Projects/Orchestrator/Decisions.md");
        harness.Vault.CreatedFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Curate_ContradictsAnExistingNote_DefersAndAsksRatherThanOverwriting()
    {
        var harness = new Harness();
        await harness.Vault.SeedAsync("Projects/Orchestrator/Decisions.md", "Orders are exposed read-write");
        harness.Agent.Proposes([DurableCandidate("Orders are exposed read-only")]);

        var result = await harness.Curator.CurateAsync(TaskId, default);

        result.Deferred.Count.ShouldBe(1);
        harness.Vault.Writes.ShouldBeEmpty();
        harness.Chat.Posted.Single().Text.ShouldContain("conflict");
    }

    [Fact]
    public async Task Curate_VaultPathOutsideTheEnvelopePrefixes_IsRefused()
    {
        var harness = new Harness();
        harness.Agent.Proposes([DurableCandidate(targetPath: "HR/Salaries.md")]);

        var result = await harness.Curator.CurateAsync(TaskId, default);

        result.Published.ShouldBeEmpty();
        harness.Vault.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Curate_ConcurrentEditDetected_DoesNotOverwriteAndRetriesOnce()
    {
        var harness = new Harness();
        harness.Vault.ReturnConflictOnFirstWrite();
        harness.Agent.Proposes([DurableCandidate()]);

        await harness.Curator.CurateAsync(TaskId, default);

        harness.Vault.WriteAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task Curate_PublishedNote_RecordsProvenanceAndIsRevertible()
    {
        var harness = new Harness();
        harness.Agent.Proposes([DurableCandidate()]);

        var result = await harness.Curator.CurateAsync(TaskId, default);

        var proposal = result.Published.Single();
        proposal.SourceTaskId.ShouldBe(TaskId);
        proposal.VaultRevisionId.ShouldNotBeNull();
        await harness.Curator.RevertAsync(proposal.Id, default);
        harness.Vault.Reverts.Single().RevisionId.ShouldBe(proposal.VaultRevisionId);
    }

    private static MemoryCandidate Candidate(string content) =>
        new(content, [new MemorySourceEvidence("file", "src/Api/OrdersController.cs@abc123", "abc123")]);

    private static MemoryCandidate DurableCandidate(string? content = null, string? targetPath = null) => new(
        content ?? "Picked orders are exposed read-only because CustomerOrder is write-only in P1",
        [new MemorySourceEvidence("file", "src/Api/OrdersController.cs@abc123", "abc123")],
        targetPath);

    private sealed class Harness
    {
        public FakeAgent Agent { get; } = new();
        public FakeVaultStore Vault { get; } = new();
        public FakeChatGateway Chat { get; } = new();
        public RecordingAuditWriter Audit { get; } = new();
        public FakeProposalStore Proposals { get; } = new();
        public FakeScopeStore Scope { get; }

        private readonly InMemoryPersonStore _people = new();

        public Harness()
        {
            _people.AddAsync(Magnus, default).GetAwaiter().GetResult();
            Scope = new FakeScopeStore(NewCompletedTask(), Envelope());
        }

        public MemoryCurator Curator => new(
            new AgentRouter([Agent], new FakeGitWorkspaces(), new FakeArtifactStore()),
            Vault,
            Proposals,
            Scope,
            new EvidenceAssembler(new FakeEvidenceStore()),
            Chat,
            Audit,
            TimeProvider.System,
            new MemoryCuratorOptions());

        private static BudgetLimits Limits() => new(25m, TimeSpan.FromHours(4), 10, 3);

        private static WorkTask NewCompletedTask()
        {
            var task = new WorkTask("objective", Magnus.Id.ToString(), TaskSource.Slack,
                "slack:C1:1737000000.000100", DateTimeOffset.UtcNow);
            typeof(WorkTask).GetProperty(nameof(WorkTask.Id))!.SetValue(task, TaskId);
            return task;
        }

        private static AuthorizationEnvelope Envelope() => new(
            TaskId,
            Magnus.Id.ToString(),
            [],
            ["staging"],
            [],
            ["replay"],
            [Domain.Authorization.AgentStage.Implementation],
            ["C1"],
            [Magnus.SlackUserId!],
            ["Projects/"],
            [],
            Limits(),
            DateTimeOffset.UtcNow.AddDays(7),
            DateTimeOffset.UtcNow);
    }

    private sealed class FakeAgent : ICodingAgent
    {
        private string _output = "{\"candidates\": []}";

        public string Name => "replay";
        public int Invocations { get; private set; }

        public void Proposes(IReadOnlyList<MemoryCandidate> candidates) =>
            _output = JsonSerializer.Serialize(new
            {
                candidates = candidates.Select(c => new
                {
                    content = c.Content,
                    targetPath = c.TargetPath,
                    evidence = c.Evidence.Select(e => new
                    {
                        kind = e.Kind,
                        identifier = e.Identifier,
                        version = e.Version,
                    }),
                }),
            });

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new AgentResult(
                AgentStatus.Succeeded, "replay", "replay-model", "abc", null, null,
                [], [], [], new AgentUsage(0m, TimeSpan.Zero), null, _output));
        }
    }

    private sealed class FakeVaultStore : IVaultStore
    {
        private readonly Dictionary<string, string> _notes = [];
        private readonly Dictionary<string, List<(string RevisionId, string Body)>> _revisions = [];
        private bool _conflictOnce;

        public List<VaultWrite> Writes { get; } = [];
        public List<string> CreatedFiles { get; } = [];
        public List<(string Path, string RevisionId)> Reverts { get; } = [];
        public int WriteAttempts { get; private set; }

        public Task SeedAsync(string path, string body)
        {
            _notes[path] = body;
            RevisionsFor(path).Add(($"rev-seed-{RevisionsFor(path).Count}", body));
            return Task.CompletedTask;
        }

        public void ReturnConflictOnFirstWrite() => _conflictOnce = true;

        public Task<IReadOnlyList<VaultNote>> SearchAsync(VaultQuery query, CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<VaultNote>>(_notes
                .Where(n => query.AllowedPathPrefixes.Any(p =>
                    n.Key.StartsWith(p, StringComparison.Ordinal)))
                .Where(n => n.Value.Contains(query.Text.Split(' ').FirstOrDefault() ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                .Select(n => new VaultNote(n.Key, TestFrontmatter(), n.Value,
                    FakeHash(n.Value), now))
                .ToList());
        }

        public Task<VaultNote> ReadAsync(string path, CancellationToken ct)
        {
            if (!_notes.TryGetValue(path, out var body))
                throw new FileNotFoundException($"Note '{path}' was not found.");
            return Task.FromResult(new VaultNote(path, TestFrontmatter(), body,
                FakeHash(body), DateTimeOffset.UtcNow));
        }

        public Task<VaultWriteResult> WriteAsync(VaultWrite write, CancellationToken ct)
        {
            WriteAttempts++;
            Writes.Add(write);
            if (_conflictOnce)
            {
                _conflictOnce = false;
                return Task.FromResult(VaultWriteResult.Conflict);
            }

            if (!_notes.ContainsKey(write.Path))
                CreatedFiles.Add(write.Path);
            _notes[write.Path] = write.Body;
            RevisionsFor(write.Path).Add(($"rev-{RevisionsFor(write.Path).Count}", write.Body));
            return Task.FromResult(VaultWriteResult.Written);
        }

        public Task<VaultWriteResult> AppendOrReplaceSectionAsync(
            string path, string sectionHeading, string sectionBody, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VaultRevision>> GetRevisionsAsync(string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VaultRevision>>(RevisionsFor(path)
                .Select(r => new VaultRevision(r.RevisionId, DateTimeOffset.UtcNow, FakeHash(r.Body)))
                .ToList());

        public Task RevertAsync(string path, string revisionId, CancellationToken ct)
        {
            Reverts.Add((path, revisionId));
            var revision = RevisionsFor(path).FirstOrDefault(r => r.RevisionId == revisionId);
            if (revision != default)
                _notes[path] = revision.Body;
            return Task.CompletedTask;
        }

        private List<(string RevisionId, string Body)> RevisionsFor(string path)
        {
            if (!_revisions.TryGetValue(path, out var list))
                _revisions[path] = list = [];
            return list;
        }

        private static NoteFrontmatter TestFrontmatter() => new(
            "Note", "reference", "engineering", "active", DateTimeOffset.UtcNow, ["orchestrator"]);

        private static string FakeHash(string body) =>
            "sha256:" + Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    private sealed class FakeChatGateway : IChatGateway
    {
        public List<ChatMessage> Posted { get; } = [];

        public Task<PostedMessage> PostAsync(ChatMessage message, CancellationToken ct)
        {
            Posted.Add(message);
            return Task.FromResult(new PostedMessage(message.ChannelId, "1000.0001", message.ThreadTs ?? "1000.0001"));
        }

        public Task<PostedMessage> PostBlocksAsync(
            string channelId, string? threadTs, IReadOnlyList<ChatBlock> blocks, string fallbackText,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task RecordAsync(AuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProposalStore : IMemoryProposalStore
    {
        private readonly Dictionary<Guid, MemoryProposal> _proposals = [];

        public Task AddAsync(MemoryProposal proposal, CancellationToken ct)
        {
            _proposals[proposal.Id] = proposal;
            return Task.CompletedTask;
        }

        public Task<MemoryProposal?> GetAsync(Guid id, CancellationToken ct)
        {
            _proposals.TryGetValue(id, out var proposal);
            return Task.FromResult(proposal);
        }
    }

    private sealed class FakeScopeStore : IPlanningScopeStore
    {
        private readonly WorkTask _task;
        private readonly AuthorizationEnvelope _envelope;

        public FakeScopeStore(WorkTask task, AuthorizationEnvelope envelope)
        {
            _task = task;
            _envelope = envelope;
        }

        public Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct) => Task.FromResult(_task);

        public Task SaveTaskAsync(WorkTask task, CancellationToken ct) => Task.CompletedTask;

        public Task<AuthorizationEnvelope?> GetEnvelopeAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<AuthorizationEnvelope?>(_envelope);

        public Task<BudgetLedger?> GetLedgerAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<BudgetLedger?>(null);

        public Task SaveLedgerAsync(BudgetLedger ledger, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeEvidenceStore : ITaskEvidenceStore
    {
        public Task<IReadOnlyList<CheckResult>> GetRuntimeChecksAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CheckResult>>([]);

        public Task<IReadOnlyList<DomainCheckResult>> GetDomainChecksAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DomainCheckResult>>([]);

        public Task<GitDiffSummary> GetDiffSummaryAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(new GitDiffSummary(string.Empty, string.Empty, [], null));

        public Task<string> GetPlanRevisionHashAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(string.Empty);

        public Task<IReadOnlyList<string>> GetUnresolvedQuestionsAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeGitWorkspaces : IGitWorkspace
    {
        public Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct) =>
            Task.FromResult(new GitDiff("abc", "abc", [], string.Empty));

        public Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DisposeAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        public Task<string> PutAsync(ArtifactDescriptor descriptor, Stream content, CancellationToken ct) =>
            Task.FromResult(Guid.CreateVersion7().ToString("N"));

        public Task<Stream> OpenAsync(string artifactId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
