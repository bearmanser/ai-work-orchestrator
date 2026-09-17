using Orchestrator.Application.Ports;
using Orchestrator.Domain.Plans;

namespace Orchestrator.Application.Plans;

/// <summary>
/// Renders a plan revision as Block Kit. Every button carries the revision
/// hash in its value, so a click can never be attributed to a different
/// revision than the one rendered.
/// </summary>
public static class PlanBlockKitBuilder
{
    public static IReadOnlyList<ChatBlock> Build(PlanRevision revision, PlanContent content) => new ChatBlock[]
    {
        new ChatBlock.SectionBlock($"Plan revision {revision.RevisionNumber} — {revision.ContentHash}"),
        new ChatBlock.SectionBlock(content.Objective),
        new ChatBlock.SectionBlock(string.Join("\n", content.RepositoryChanges.Select(DescribeChange))),
        new ChatBlock.ActionsBlock(
        [
            new ChatAction("confirm_plan", "Confirm plan", revision.ContentHash, ChatActionStyle.Primary),
            new ChatAction("request_plan_changes", "Request changes", revision.ContentHash, ChatActionStyle.Default),
            new ChatAction("cancel_task", "Cancel task", revision.ContentHash, ChatActionStyle.Danger),
        ]),
    };

    private static string DescribeChange(PlannedRepositoryChange change) =>
        change.Intent == ChangeIntent.Change
            ? $"{change.RepositoryName} — will change"
            : $"{change.RepositoryName} — reference only";
}
