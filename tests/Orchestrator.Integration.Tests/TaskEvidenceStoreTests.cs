using System.Text.Json;
using Orchestrator.Application.Evidence;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;
using Orchestrator.Infrastructure.Artifacts;
using Orchestrator.Infrastructure.Persistence;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class TaskEvidenceStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Assemble_ReadsChecksAndDiffFromTheVerificationStepArtifact()
    {
        var taskId = Guid.CreateVersion7();
        var stepId = Guid.CreateVersion7();
        var evidence = new TaskEvidence(
            [new CheckResult("dotnet test", true, 0, TimeSpan.FromMinutes(1), "a1", CheckOrigin.RuntimeExecuted)],
            [new DomainCheckResult("picked-order-count", "staging", "KAMELEON-TEST", true, [], "ok")],
            new GitDiffSummary("abc", "def", ["src/Orders.cs"], null),
            "sha256:abc123",
            []);

        string artifactId;
        var artifactRoot =
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evidence-store-tests-" + Guid.NewGuid())).FullName;
        await using (var db = fixture.NewDbContext())
        {
            var artifacts = new FileSystemArtifactStore(artifactRoot, db, TimeProvider.System);
            await using var content = new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evidence)));
            artifactId = await artifacts.PutAsync(
                new ArtifactDescriptor(taskId, "task-evidence", "application/json",
                    ArtifactSensitivity.Internal, "verification-step"),
                content, default);

            var step = new Domain.Steps.Step(taskId, "verification", null, null, DateTimeOffset.UtcNow);
            db.Steps.Add(step);
            step.Succeed(artifactId, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(default);
        }

        await using var readDb = fixture.NewDbContext();
        var store = new EfTaskEvidenceStore(
            readDb,
            new FileSystemArtifactStore(artifactRoot, readDb, TimeProvider.System),
            new UnusableConfirmationStore(),
            new UnusableQuestionStore());

        // The store reads checks, domain checks, and the diff from the artifact the
        // verification step recorded; only the plan hash and questions come from elsewhere.
        var assembled = new TaskEvidence(
            await store.GetRuntimeChecksAsync(taskId, default),
            await store.GetDomainChecksAsync(taskId, default),
            await store.GetDiffSummaryAsync(taskId, default),
            await store.GetPlanRevisionHashAsync(taskId, default),
            await store.GetUnresolvedQuestionsAsync(taskId, default));

        assembled.RuntimeChecks.Single().Command.ShouldBe("dotnet test");
        assembled.DomainChecks.Single().QueryName.ShouldBe("picked-order-count");
        assembled.Diff.ChangedFiles.ShouldBe(["src/Orders.cs"]);
        assembled.PlanRevisionHash.ShouldBe(string.Empty);
        assembled.UnresolvedQuestions.ShouldBeEmpty();
    }

    private sealed class UnusableConfirmationStore : Application.Plans.IPlanConfirmationStore
    {
        public Task<Domain.Tasks.WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SaveTaskAsync(Domain.Tasks.WorkTask task, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AddRevisionAsync(Domain.Plans.PlanRevision revision, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Domain.Plans.PlanRevision> GetRevisionAsync(Guid revisionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Domain.Plans.PlanRevision?> GetLatestPendingRevisionAsync(Guid taskId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Domain.Plans.PlanRevision>> ListRevisionsAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Domain.Plans.PlanRevision>>([]);

        public Task<Domain.Plans.PlanRevision?> FindByHashAsync(string contentHash, CancellationToken ct) =>
            Task.FromResult<Domain.Plans.PlanRevision?>(null);
    }

    private sealed class UnusableQuestionStore : IHumanQuestionStore
    {
        public Task AddAsync(Domain.Humans.HumanQuestion question, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Domain.Humans.HumanQuestion?> GetAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Domain.Humans.HumanQuestion?> FindByChannelThreadAsync(string channelId, string threadTs, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Domain.Humans.HumanQuestion>> ListOpenAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Domain.Humans.HumanQuestion>>([]);

        public Task SaveAsync(Domain.Humans.HumanQuestion question, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
