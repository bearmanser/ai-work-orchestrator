using Orchestrator.Application.Memory;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class DurabilityRulesTests
{
    [Fact]
    public void IsDurable_AnArchitecturalDecisionWithEvidence_IsDurable() =>
        DurabilityRules.IsDurable(Candidate(
            "Picked orders are exposed read-only because CustomerOrder is write-only in P1",
            evidence: [SourceEvidence("src/Api/OrdersController.cs", "abc123")])).Durable.ShouldBeTrue();

    [Fact]
    public void IsDurable_ARecurringFailureAndItsProvenRemedy_IsDurable() =>
        DurabilityRules.IsDurable(Candidate(
            "Integration tests fail unless the staging company code is set; set KAMELEON-TEST",
            evidence: [CheckEvidence("dotnet test", passed: true)])).Durable.ShouldBeTrue();

    [Fact]
    public void IsDurable_ARawTaskLog_IsNotDurable() =>
        DurabilityRules.IsDurable(Candidate("Ran dotnet test at 10:04 and it passed")).Durable.ShouldBeFalse();

    [Fact]
    public void IsDurable_AnUnsupportedClaim_IsNotDurable() =>
        DurabilityRules.IsDurable(Candidate("The team prefers CQRS", evidence: [])).Durable.ShouldBeFalse();

    [Fact]
    public void IsDurable_ContentContainingASecretShape_IsNotDurable() =>
        DurabilityRules.IsDurable(Candidate("Use token ghp_abcdefghijklmnopqrstuvwxyz0123456789"))
            .Durable.ShouldBeFalse();

    [Fact]
    public void IsDurable_AnAgentTranscriptExcerpt_IsNotDurable() =>
        DurabilityRules.IsDurable(Candidate("I will now read the file and then edit it")).Durable.ShouldBeFalse();

    private static MemoryCandidate Candidate(string content, IReadOnlyList<MemorySourceEvidence>? evidence = null) =>
        new(content, evidence ?? [SourceEvidence("src/Api/OrdersController.cs", "abc123")]);

    private static MemorySourceEvidence SourceEvidence(string path, string commit) =>
        new("file", $"{path}@{commit}", commit);

    private static MemorySourceEvidence CheckEvidence(string command, bool passed) =>
        new("check", $"{command}:{(passed ? "passed" : "failed")}", null);
}
