namespace Orchestrator.Application.Ports;

public enum ChatActionStyle
{
    Default,
    Primary,
    Danger,
}

public sealed record ChatAction(string ActionId, string Label, string Value, ChatActionStyle Style);

/// <summary>
/// A small Block Kit hierarchy for interactive messages. Plain-text posting
/// stays on <see cref="IChatGateway.PostAsync"/>; these blocks are for the
/// richer confirmations built on top of the same transport.
/// </summary>
public abstract record ChatBlock
{
    private ChatBlock() { }

    public sealed record SectionBlock(string Markdown) : ChatBlock;

    public sealed record ActionsBlock(IReadOnlyList<ChatAction> Actions) : ChatBlock;

    public sealed record ContextBlock(string Markdown) : ChatBlock;
}

public sealed record ChatMessage(
    string ChannelId,
    string? ThreadTs,
    string? RecipientSlackUserId,
    string Text);

public sealed record PostedMessage(string ChannelId, string MessageTs, string ThreadTs);

/// <summary>
/// Outbound chat transport. Implementations post through the workspace's chat
/// provider; the human question loop only depends on this port, never on Slack.
/// </summary>
public interface IChatGateway
{
    Task<PostedMessage> PostAsync(ChatMessage message, CancellationToken ct);

    Task<PostedMessage> PostBlocksAsync(
        string channelId,
        string? threadTs,
        IReadOnlyList<ChatBlock> blocks,
        string fallbackText,
        CancellationToken ct);
}
