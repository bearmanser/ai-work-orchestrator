namespace Orchestrator.Application.Slack;

public enum SlackInboundKind
{
    AppMention,
    DirectMessage,
    ThreadReply,
    BlockAction,
}

/// <summary>
/// A Slack event worth handling downstream. Defined in Application (not the
/// Slack adapter) so classifiers and intake can consume it without depending
/// on an adapter assembly.
/// </summary>
public sealed record SlackInbound(
    string EventId,
    SlackInboundKind Kind,
    string ChannelId,
    string? ThreadTs,
    string MessageTs,
    string UserId,
    string Text);
