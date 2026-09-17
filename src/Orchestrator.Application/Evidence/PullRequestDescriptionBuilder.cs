using System.Text;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Plans;

namespace Orchestrator.Application.Evidence;

/// <summary>
/// Builds PR descriptions with a fixed section order. Local and P1 checks are
/// rendered by separate methods from separate types, so no refactor can
/// accidentally merge them — and a local test can never be presented as
/// staging verification.
/// </summary>
public static class PullRequestDescriptionBuilder
{
    public static string Build(
        TaskEvidence evidence, PlanRevision plan, IReadOnlyList<PullRequestRef> siblings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## What changed");
        sb.AppendLine(plan.Content.Objective).AppendLine();
        foreach (var change in plan.Content.RepositoryChanges)
            sb.AppendLine($"- {change.RepositoryName} ({change.Intent}) at {change.BaselineCommit}");
        sb.AppendLine();

        sb.AppendLine("## Confirmed plan");
        sb.AppendLine(plan.Content.Objective);
        sb.AppendLine($"Revision hash: {plan.ContentHash}").AppendLine();

        sb.Append(RenderLocalChecks(evidence.RuntimeChecks));
        sb.Append(RenderDomainChecks(evidence.DomainChecks));

        sb.AppendLine("## Related pull requests");
        if (siblings.Count == 0)
        {
            sb.AppendLine("None.").AppendLine();
        }
        else
        {
            foreach (var sibling in siblings)
            {
                sb.AppendLine($"- PR {sibling.Id} ({sibling.SourceBranch} → {sibling.TargetBranch}): {sibling.Url}");
                sb.AppendLine("  This PR must also be approved before the task completes.");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Open questions");
        if (evidence.UnresolvedQuestions.Count == 0)
        {
            sb.AppendLine("None.").AppendLine();
        }
        else
        {
            foreach (var question in evidence.UnresolvedQuestions)
                sb.AppendLine($"- {question}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string RenderLocalChecks(IReadOnlyList<CheckResult> checks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Local checks");
        foreach (var check in checks)
        {
            var outcome = check.Passed ? "PASSED" : "FAILED";
            sb.AppendLine($"- {check.Command}: {outcome} in {check.Duration:mm\\:ss}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string RenderDomainChecks(IReadOnlyList<DomainCheckResult> checks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## P1 checks");
        if (checks.Count == 0)
        {
            sb.AppendLine("No P1 validation was performed.").AppendLine();
            return sb.ToString();
        }

        foreach (var check in checks)
        {
            var outcome = check.Passed ? "PASSED" : "FAILED";
            sb.AppendLine($"- {check.QueryName} ({check.Environment}/{check.Company}): {outcome}");
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
