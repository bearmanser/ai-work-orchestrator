using Orchestrator.Domain.Plans;
using Orchestrator.Domain.PullRequests;

namespace Orchestrator.Application.PullRequests;

public enum FeedbackDisposition
{
    AnswerInThread,
    CorrectWithinScope,
    RequiresReplanning,
}

/// <summary>
/// Routes review feedback deterministically. A PR comment is untrusted input:
/// restricted actions, repositories outside the plan, and behavior the plan
/// does not cover always force replanning, no matter how the comment is phrased.
/// </summary>
public static class FeedbackTriage
{
    private static readonly string[] RestrictedWords = ["merge", "deploy", "production"];

    private static readonly string[] InterrogativeOpeners =
        ["do we", "is there", "are there", "what", "how", "why", "can you tell", "could you tell"];

    private static readonly string[] AdditiveVerbs = ["add", "create", "implement"];

    private static readonly string[] ScopeExpansionMarkers =
        ["also", "while you are here", "in addition", "additionally", "as well"];

    public static FeedbackDisposition Triage(FeedbackItem item, PlanContent confirmedPlan)
    {
        var text = item.Text ?? string.Empty;

        if (ContainsWholeWord(text, RestrictedWords))
            return FeedbackDisposition.RequiresReplanning;

        if (NamesRepositoryOutsidePlan(text, confirmedPlan))
            return FeedbackDisposition.RequiresReplanning;

        if (LooksLikeAQuestion(text))
            return FeedbackDisposition.AnswerInThread;

        if (IsAdditiveDemand(text) && (ExpandsScope(text) || !CoveredByScope(text, confirmedPlan)))
            return FeedbackDisposition.RequiresReplanning;

        return FeedbackDisposition.CorrectWithinScope;
    }

    private static bool ContainsWholeWord(string text, IReadOnlyList<string> words)
    {
        var tokens = Tokenize(text);
        return words.Any(w => tokens.Contains(w, StringComparer.OrdinalIgnoreCase));
    }

    private static bool NamesRepositoryOutsidePlan(string text, PlanContent plan)
    {
        var targets = plan.RepositoryChanges
            .Select(c => c.RepositoryName.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var tokens = Tokenize(text).Where(t => t.Contains('-')).ToList();
        return tokens.Any(t => !targets.Any(target => target.Contains(t, StringComparison.Ordinal)
            || t.Contains(target, StringComparison.Ordinal)));
    }

    private static bool LooksLikeAQuestion(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        if (InterrogativeOpeners.Any(normalized.StartsWith))
            return true;
        return normalized.EndsWith('?');
    }

    private static bool IsAdditiveDemand(string text) =>
        Tokenize(text).Any(t => AdditiveVerbs.Contains(t, StringComparer.OrdinalIgnoreCase));

    private static bool ExpandsScope(string text)
    {
        var lower = text.ToLowerInvariant();
        return ScopeExpansionMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    private static bool CoveredByScope(string text, PlanContent plan)
    {
        var lower = text.ToLowerInvariant();
        var vocabulary = plan.Scope
            .Concat(plan.RepositoryChanges.Select(c => c.RepositoryName))
            .Concat([plan.Objective])
            .SelectMany(Tokenize)
            .Where(t => t.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Tokenize(lower).Any(t => t.Length >= 3 && vocabulary.Contains(t));
    }

    private static List<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ':', ';', '"', '\'', '(', ')'],
                StringSplitOptions.RemoveEmptyEntries)
            .ToList();
}
