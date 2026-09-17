using System.Text.Json;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Agent.Providers;

/// <summary>
/// Folds the Codex CLI's JSONL event stream (one JSON object per line, as opposed to Claude's
/// single JSON envelope) into an <see cref="AgentResult"/>.
/// </summary>
public static class CodexEventStreamParser
{
    /// <summary>
    /// Parses a Codex <c>exec --json</c> event stream.
    /// </summary>
    /// <param name="jsonl">The raw stdout captured from the sandboxed Codex CLI invocation.</param>
    /// <param name="baselineCommit">The commit the workspace was checked out at before this run.</param>
    /// <param name="model">The model name configured for this invocation (Codex events don't echo it back).</param>
    public static AgentResult Parse(string jsonl, string baselineCommit, string model)
    {
        var claimedChecks = new List<ClaimedCheck>();
        string? lastAgentMessage = null;
        AgentStatus? status = null;
        string? failureDetail = null;
        var inputTokens = 0;
        var outputTokens = 0;

        foreach (var rawLine in jsonl.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // A stray non-JSON line (e.g. a stderr banner interleaved into stdout) must not
                // break parsing of the events around it.
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProperty))
                    continue;

                switch (typeProperty.GetString())
                {
                    case "item.completed":
                        HandleItemCompleted(root, claimedChecks, ref lastAgentMessage);
                        break;

                    case "turn.completed":
                        status = AgentStatus.Succeeded;
                        (inputTokens, outputTokens) = ReadUsage(root);
                        break;

                    case "turn.failed":
                        status = AgentStatus.Failed;
                        failureDetail = ReadErrorMessage(root);
                        break;

                    default:
                        // Unknown/forward-compatible event types (including this session's own
                        // "session.created") are ignored rather than treated as fatal, so a future
                        // Codex CLI version emitting event types this parser has never seen does
                        // not break the fold.
                        break;
                }
            }
        }

        // The stream ending with neither a turn.completed nor a turn.failed event means the turn
        // was cut off (e.g. by a timeout upstream), not that it succeeded or failed cleanly.
        status ??= AgentStatus.Partial;

        return new AgentResult(
            Status: status.Value,
            Provider: "codex",
            Model: model,
            BaselineCommit: baselineCommit,
            ResultCommit: null,
            DiffArtifactId: null,
            ClaimedChecks: claimedChecks,
            UnresolvedQuestions: [],
            // The last agent_message item is the agent's own authoritative account of what it did
            // (and didn't) do - if it revises its summary mid-turn, the final one wins.
            Limitations: lastAgentMessage is null ? [] : [lastAgentMessage],
            Usage: new AgentUsage(CostUsd: 0m, Duration: TimeSpan.Zero, InputTokens: inputTokens, OutputTokens: outputTokens),
            FailureDetail: failureDetail);
    }

    private static void HandleItemCompleted(
        JsonElement root, List<ClaimedCheck> claimedChecks, ref string? lastAgentMessage)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return;

        if (!item.TryGetProperty("type", out var itemTypeProperty))
            return;

        switch (itemTypeProperty.GetString())
        {
            case "command_execution":
                var command = item.TryGetProperty("command", out var commandProperty)
                    ? commandProperty.GetString() ?? string.Empty
                    : string.Empty;
                var exitCode = item.TryGetProperty("exit_code", out var exitCodeProperty)
                    ? exitCodeProperty.GetInt32()
                    : -1;
                claimedChecks.Add(new ClaimedCheck(command, ClaimedPassed: exitCode == 0));
                break;

            case "agent_message":
                if (item.TryGetProperty("text", out var textProperty))
                    lastAgentMessage = textProperty.GetString();
                break;

            default:
                // Unknown item types (e.g. reasoning, file_change in future CLI versions) are
                // ignored, not fatal.
                break;
        }
    }

    private static (int InputTokens, int OutputTokens) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (0, 0);

        var inputTokens = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : 0;
        var outputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : 0;
        return (inputTokens, outputTokens);
    }

    private static string? ReadErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;

        return error.TryGetProperty("message", out var message) ? message.GetString() : null;
    }
}
