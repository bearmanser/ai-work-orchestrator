using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Slack;

/// <summary>
/// Posts to Slack via <c>chat.postMessage</c> with a Bearer bot token. The token
/// is resolved per call from <see cref="ISecretStore"/> and never stored on the
/// instance; error messages carry status and route only, never the token.
/// </summary>
public sealed class SlackChatGateway(HttpClient httpClient, ISecretStore secrets) : IChatGateway
{
    private static readonly SecretRef BotTokenRef = new("slack.bot-token");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<PostedMessage> PostAsync(ChatMessage message, CancellationToken ct) =>
        SendAsync(message.ChannelId, message.ThreadTs, message.Text, blocks: null, ct);

    public Task<PostedMessage> PostBlocksAsync(
        string channelId,
        string? threadTs,
        IReadOnlyList<ChatBlock> blocks,
        string fallbackText,
        CancellationToken ct) =>
        SendAsync(channelId, threadTs, fallbackText, blocks, ct);

    private async Task<PostedMessage> SendAsync(
        string channelId,
        string? threadTs,
        string text,
        IReadOnlyList<ChatBlock>? blocks,
        CancellationToken ct)
    {
        var token = await secrets.GetAsync(BotTokenRef, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat.postMessage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var payload = new Dictionary<string, object?>
        {
            ["channel"] = channelId,
            ["text"] = text,
            ["thread_ts"] = threadTs,
        };
        if (blocks is not null)
            payload["blocks"] = blocks.Select(SerializeBlock).ToList();

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Slack chat.postMessage failed with {(int)response.StatusCode} {response.StatusCode}.",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok)
            || ok.ValueKind != JsonValueKind.True
            || !ok.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : "unknown_error";
            throw new HttpRequestException($"Slack chat.postMessage reported failure: {error}.");
        }

        if (!root.TryGetProperty("ts", out var tsElement)
            || tsElement.GetString() is not { Length: > 0 } ts)
            throw new HttpRequestException("Slack chat.postMessage succeeded without a message timestamp.");

        return new PostedMessage(channelId, ts, threadTs ?? ts);
    }

    private static object SerializeBlock(ChatBlock block) => block switch
    {
        ChatBlock.SectionBlock section => new
        {
            type = "section",
            text = new { type = "mrkdwn", text = section.Markdown },
        },
        ChatBlock.ActionsBlock actions => new
        {
            type = "actions",
            elements = actions.Actions.Select(SerializeAction).ToList(),
        },
        ChatBlock.ContextBlock context => new
        {
            type = "context",
            elements = new object[] { new { type = "mrkdwn", text = context.Markdown } },
        },
        _ => throw new InvalidOperationException($"Unknown chat block type: {block.GetType()}."),
    };

    private static object SerializeAction(ChatAction action) => new
    {
        type = "button",
        action_id = action.ActionId,
        text = new { type = "plain_text", text = action.Label },
        value = action.Value,
        style = action.Style switch
        {
            ChatActionStyle.Primary => "primary",
            ChatActionStyle.Danger => "danger",
            _ => null,
        },
    };
}
