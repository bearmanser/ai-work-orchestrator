using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;

namespace Orchestrator.Application.Checks;

public sealed record ClaimComparison(bool HasMismatch, IReadOnlyList<string> Mismatches);

/// <summary>
/// Compares what an agent <i>claimed</i> about checks against what the runtime
/// actually observed. Claims are never evidence on their own.
/// </summary>
public static class CheckClaimComparer
{
    public static ClaimComparison Compare(
        IReadOnlyList<ClaimedCheck> claims, IReadOnlyList<CheckResult> actual)
    {
        var mismatches = new List<string>();

        foreach (var claim in claims)
        {
            var observed = actual.FirstOrDefault(r =>
                r.Command.Equals(claim.Command, StringComparison.Ordinal));
            if (observed is null)
            {
                mismatches.Add(
                    $"'{claim.Command}' was claimed but not executed by the runtime.");
            }
            else if (claim.ClaimedPassed && !observed.Passed)
            {
                mismatches.Add(
                    $"'{claim.Command}': claimed pass, runtime observed fail.");
            }
            else if (!claim.ClaimedPassed && observed.Passed)
            {
                mismatches.Add(
                    $"'{claim.Command}': claimed fail, runtime observed pass.");
            }
        }

        return new ClaimComparison(mismatches.Count > 0, mismatches);
    }
}
