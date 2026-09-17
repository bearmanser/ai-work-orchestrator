using Orchestrator.Application.Checks;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class CheckClaimComparerTests
{
    [Fact]
    public void Compare_AgentClaimedPassAndRuntimeFailed_IsAMismatch()
    {
        var comparison = CheckClaimComparer.Compare(
            [new ClaimedCheck("dotnet test", ClaimedPassed: true)],
            [Result("dotnet test", passed: false)]);

        comparison.HasMismatch.ShouldBeTrue();
        comparison.Mismatches.Single().ShouldContain("claimed pass, runtime observed fail");
    }

    [Fact]
    public void Compare_AgentClaimedNothing_RuntimeResultStillStands()
    {
        var comparison = CheckClaimComparer.Compare([], [Result("dotnet test", passed: true)]);

        comparison.HasMismatch.ShouldBeFalse();
    }

    [Fact]
    public void Compare_AgentClaimedACheckTheRuntimeNeverRan_IsAMismatch()
    {
        var comparison = CheckClaimComparer.Compare(
            [new ClaimedCheck("npm run e2e", true)], [Result("dotnet test", true)]);

        comparison.HasMismatch.ShouldBeTrue();
        comparison.Mismatches.Single().ShouldContain("not executed by the runtime");
    }

    [Fact]
    public void Compare_BothAgree_IsNotAMismatch() =>
        CheckClaimComparer.Compare([new ClaimedCheck("dotnet test", true)], [Result("dotnet test", true)])
            .HasMismatch.ShouldBeFalse();

    private static CheckResult Result(string command, bool passed) => new(
        command, passed, passed ? 0 : 1, TimeSpan.FromSeconds(1), "artifact-1", CheckOrigin.RuntimeExecuted);
}
