using System.Text.Json;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Agent.Providers;

/// <summary>
/// Parses the JSON result envelope the Claude CLI prints (<c>--output-format json</c>) into an
/// <see cref="AgentResult"/>.
/// </summary>
public static class ClaudeResultParser
{
    private const string ProviderName = "claude";

    /// <summary>
    /// Commands this heuristic looks for in the free-text "result" field to populate
    /// <see cref="AgentResult.ClaimedChecks"/>. This is a heuristic only - it tells an operator
    /// what the agent said it ran, never a verified outcome. Task 29's runtime CheckRunner is the
    /// actual evidence.
    /// </summary>
    private static readonly string[] CandidateCommands =
    [
        "dotnet test", "dotnet build", "dotnet format",
        "npm test", "npm run build", "npm run lint",
        "yarn test", "pytest", "make test", "cargo test", "go test",
    ];

    public static AgentResult Parse(string rawOutput, string baselineCommit, string model)
    {
        var envelope = ExtractEnvelope(rawOutput)
            ?? throw new AgentProtocolException(
                "Claude CLI output did not contain a parseable JSON result envelope.", rawOutput);

        var subtype = envelope.TryGetProperty("subtype", out var subtypeProp) ? subtypeProp.GetString() : null;
        var isError = envelope.TryGetProperty("is_error", out var isErrorProp) && isErrorProp.GetBoolean() is true;
        var resultText = envelope.TryGetProperty("result", out var resultProp) ? resultProp.GetString() ?? "" : "";

        var status = MapStatus(subtype, isError);

        var costUsd = envelope.TryGetProperty("total_cost_usd", out var costProp) ? costProp.GetDecimal() : 0m;
        var durationMs = envelope.TryGetProperty("duration_ms", out var durationProp) ? durationProp.GetInt64() : 0L;
        var inputTokens = 0;
        var outputTokens = 0;
        if (envelope.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object)
        {
            if (usageProp.TryGetProperty("input_tokens", out var inputProp))
                inputTokens = inputProp.GetInt32();
            if (usageProp.TryGetProperty("output_tokens", out var outputProp))
                outputTokens = outputProp.GetInt32();
        }

        var usage = new AgentUsage(costUsd, TimeSpan.FromMilliseconds(durationMs), inputTokens, outputTokens);
        var claimedChecks = ExtractClaimedChecks(resultText);

        return new AgentResult(
            Status: status,
            Provider: ProviderName,
            Model: model,
            BaselineCommit: baselineCommit,
            ResultCommit: null,
            DiffArtifactId: null,
            ClaimedChecks: claimedChecks,
            UnresolvedQuestions: [],
            Limitations: [],
            Usage: usage,
            FailureDetail: status == AgentStatus.Succeeded ? null : resultText);
    }

    private static AgentStatus MapStatus(string? subtype, bool isError) => subtype switch
    {
        "success" when !isError => AgentStatus.Succeeded,
        "error_max_turns" => AgentStatus.Partial,
        _ when isError => AgentStatus.Failed,
        _ => AgentStatus.Succeeded,
    };

    private static IReadOnlyList<ClaimedCheck> ExtractClaimedChecks(string resultText)
    {
        List<ClaimedCheck> checks = [];
        foreach (var command in CandidateCommands)
        {
            var index = resultText.IndexOf(command, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            var windowStart = Math.Max(0, index - 20);
            var windowEnd = Math.Min(resultText.Length, index + command.Length + 20);
            var window = resultText[windowStart..windowEnd];
            var claimedPassed = !window.Contains("fail", StringComparison.OrdinalIgnoreCase)
                && !window.Contains("error", StringComparison.OrdinalIgnoreCase);

            checks.Add(new ClaimedCheck(command, claimedPassed));
        }

        return checks;
    }

    /// <summary>
    /// Finds the JSON result envelope in <paramref name="text"/>, tolerating leading noise (e.g. an
    /// npm banner) by scanning backward from the last <c>{</c> character until it finds one that
    /// opens an object balanced all the way to the end of the text.
    /// </summary>
    private static JsonElement? ExtractEnvelope(string text)
    {
        List<int> openBraceIndexes = [];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
                openBraceIndexes.Add(i);
        }

        for (var n = openBraceIndexes.Count - 1; n >= 0; n--)
        {
            var candidate = text[openBraceIndexes[n]..];
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not a balanced/valid JSON value starting here (e.g. an inner object whose close
                // brace is followed by more of the outer object); keep scanning further left.
            }
        }

        return null;
    }
}
