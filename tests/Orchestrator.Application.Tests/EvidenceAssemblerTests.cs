using Orchestrator.Application.Evidence;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class EvidenceAssemblerTests
{
    [Fact]
    public async Task Assemble_ComposesPiecesFromTheStore()
    {
        var taskId = Guid.CreateVersion7();
        var store = new FakeEvidenceStore(
            runtimeChecks: [new CheckResult("dotnet test", true, 0, TimeSpan.FromMinutes(1), "a1", CheckOrigin.RuntimeExecuted)],
            planHash: "sha256:abc123");
        var assembler = new EvidenceAssembler(store);

        var evidence = await assembler.AssembleAsync(taskId, default);

        evidence.RuntimeChecks.Single().Command.ShouldBe("dotnet test");
        evidence.PlanRevisionHash.ShouldBe("sha256:abc123");
    }

    [Fact]
    public async Task Assemble_WithNothingStored_ReturnsEmptyEvidence()
    {
        var evidence = await new EvidenceAssembler(new FakeEvidenceStore()).AssembleAsync(Guid.CreateVersion7(), default);

        evidence.RuntimeChecks.ShouldBeEmpty();
        evidence.DomainChecks.ShouldBeEmpty();
        evidence.UnresolvedQuestions.ShouldBeEmpty();
        evidence.PlanRevisionHash.ShouldBe(string.Empty);
    }

    private sealed class FakeEvidenceStore(
        IReadOnlyList<CheckResult>? runtimeChecks = null,
        IReadOnlyList<DomainCheckResult>? domainChecks = null,
        GitDiffSummary? diff = null,
        string planHash = "",
        IReadOnlyList<string>? questions = null) : ITaskEvidenceStore
    {
        public Task<IReadOnlyList<CheckResult>> GetRuntimeChecksAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(runtimeChecks ?? []);

        public Task<IReadOnlyList<DomainCheckResult>> GetDomainChecksAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(domainChecks ?? []);

        public Task<GitDiffSummary> GetDiffSummaryAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(diff ?? new GitDiffSummary(string.Empty, string.Empty, [], null));

        public Task<string> GetPlanRevisionHashAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(planHash);

        public Task<IReadOnlyList<string>> GetUnresolvedQuestionsAsync(Guid taskId, CancellationToken ct) =>
            Task.FromResult(questions ?? []);
    }
}
