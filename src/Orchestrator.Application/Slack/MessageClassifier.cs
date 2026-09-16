namespace Orchestrator.Application.Slack;

public enum MessageKind
{
    TaskRequest,
    QuestionAnswer,
    ApprovalResponse,
    Clarification,
    Unrelated,
}

/// <summary>
/// Stored correlation for one inbound message. Comes from persisted thread and
/// message ids, never from text similarity.
/// </summary>
public sealed record ConversationContext(
    Guid? TaskIdForThread,
    Guid? PendingQuestionId,
    Guid? PendingApprovalId);

/// <summary>
/// Deterministic, context-first classification. Stored correlation decides;
/// message text is only consulted to keep a question from silently becoming
/// work.
/// </summary>
public static class MessageClassifier
{
    private static readonly string[] InterrogativeOpeners =
        ["do we", "is there", "are there", "what", "how", "why", "can you tell", "could you tell"];

    private static readonly string[] ImperativeVerbs =
        ["add", "fix", "implement", "change", "remove", "update", "create"];

    public static MessageKind Classify(SlackInbound inbound, ConversationContext context)
    {
        if (inbound.Kind == SlackInboundKind.BlockAction && context.PendingApprovalId is not null)
            return MessageKind.ApprovalResponse;

        if (inbound.Kind == SlackInboundKind.ThreadReply && context.PendingQuestionId is not null)
            return MessageKind.QuestionAnswer;

        if (inbound.Kind == SlackInboundKind.ThreadReply && context.TaskIdForThread is not null)
            return MessageKind.Clarification;

        if (IsAddressed(inbound) && context.TaskIdForThread is null && !LooksLikeAQuestion(inbound.Text))
            return MessageKind.TaskRequest;

        return MessageKind.Unrelated;
    }

    private static bool IsAddressed(SlackInbound inbound) =>
        inbound.Kind is SlackInboundKind.AppMention or SlackInboundKind.DirectMessage;

    internal static bool LooksLikeAQuestion(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        if (InterrogativeOpeners.Any(normalized.StartsWith))
            return !ContainsImperative(normalized);
        return normalized.EndsWith('?') && !ContainsImperative(normalized);
    }

    private static bool ContainsImperative(string normalizedText) =>
        normalizedText
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', ':', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Any(word => ImperativeVerbs.Contains(word, StringComparer.Ordinal));
}
