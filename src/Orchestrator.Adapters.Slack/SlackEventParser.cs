using System.Text.Json;

namespace Orchestrator.Adapters.Slack;

public enum SlackInboundKind
{
    AppMention,
    DirectMessage,
    ThreadReply,
    BlockAction,
}

/// <summary>
/// A Slack event worth enqueueing for downstream handling.
/// </summary>
public sealed record SlackInbound(
    string EventId,
    SlackInboundKind Kind,
    string ChannelId,
    string? ThreadTs,
    string MessageTs,
    string UserId,
    string Text);

/// <summary>
/// Parses Slack <c>event_callback</c> payloads. <c>app_mention</c> maps to
/// <see cref="SlackInboundKind.AppMention"/>, <c>message</c> events in a thread to
/// <see cref="SlackInboundKind.ThreadReply"/>, direct messages to
/// <see cref="SlackInboundKind.DirectMessage"/>. Returns <c>null</c> for anything
/// else so the caller can acknowledge the delivery without enqueuing work.
/// </summary>
public static class SlackEventParser
{
    public static SlackInbound? Parse(JsonDocument payload)
    {
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetString(root, "type", out var type) || type != "event_callback")
            return null;

        if (!TryGetString(root, "event_id", out var eventId) || string.IsNullOrWhiteSpace(eventId))
            return null;

        if (!root.TryGetProperty("event", out var @event) || @event.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetString(@event, "type", out var eventType))
            return null;

        return eventType switch
        {
            "app_mention" => Build(eventId, SlackInboundKind.AppMention, @event),
            "message" => ParseMessage(eventId, @event),
            _ => null,
        };
    }

    private static SlackInbound? ParseMessage(string eventId, JsonElement @event)
    {
        if (TryGetString(@event, "thread_ts", out _))
            return Build(eventId, SlackInboundKind.ThreadReply, @event);

        if (@event.TryGetProperty("channel_type", out var channelType)
            && channelType.ValueKind == JsonValueKind.String
            && channelType.GetString() == "im")
            return Build(eventId, SlackInboundKind.DirectMessage, @event);

        return null;
    }

    private static SlackInbound? Build(string eventId, SlackInboundKind kind, JsonElement @event)
    {
        if (!TryGetString(@event, "channel", out var channelId)
            || !TryGetString(@event, "ts", out var messageTs)
            || !TryGetString(@event, "user", out var userId))
            return null;

        string? threadTs = null;
        if (TryGetString(@event, "thread_ts", out var rawThreadTs))
            threadTs = rawThreadTs;

        var text = TryGetString(@event, "text", out var rawText) ? rawText : string.Empty;

        return new SlackInbound(eventId, kind, channelId, threadTs, messageTs, userId, text);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;

        var text = property.GetString();
        if (text is null)
            return false;

        value = text;
        return true;
    }
}
