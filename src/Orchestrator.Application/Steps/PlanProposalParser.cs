using System.Text.Json;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Plans;

namespace Orchestrator.Application.Steps;

/// <summary>Thrown when the agent did not return the required JSON plan block.</summary>
public sealed class AgentProtocolException(string message) : Exception(message);

public sealed record ProposedQuestion(string Question, string? Domain);

public sealed record PlanProposal(PlanContent Content, IReadOnlyList<ProposedQuestion> Questions);

/// <summary>
/// Parses an agent's plan proposal from the fenced ```json block in its output.
/// The agent never sets the budget: the runtime stamps the envelope's budget
/// onto the content after parsing.
/// </summary>
public static class PlanProposalParser
{
    public static PlanProposal Parse(string agentOutput)
    {
        const string fence = "```json";
        var start = agentOutput.IndexOf(fence, StringComparison.Ordinal);
        if (start < 0)
            throw new AgentProtocolException("Agent output contains no fenced ```json plan block.");
        var end = agentOutput.IndexOf("```", start + fence.Length, StringComparison.Ordinal);
        if (end < 0)
            throw new AgentProtocolException("Agent output has an unterminated ```json plan block.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(agentOutput.Substring(start + fence.Length, end - start - fence.Length));
        }
        catch (JsonException ex)
        {
            throw new AgentProtocolException($"Agent plan block is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            try
            {
                return new PlanProposal(ReadContent(root), ReadQuestions(root));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
            {
                throw new AgentProtocolException($"Agent plan block is missing required fields: {ex.Message}");
            }
        }
    }

    private static PlanContent ReadContent(JsonElement root) => new(
        Objective: root.GetProperty("objective").GetString() ?? throw new InvalidOperationException("objective"),
        Scope: root.GetProperty("scope").EnumerateArray().Select(e => e.GetString()!).ToList(),
        Exclusions: OptionalStrings(root, "exclusions"),
        Approach: root.TryGetProperty("approach", out var approach) ? approach.GetString() ?? string.Empty : string.Empty,
        RepositoryChanges: root.GetProperty("repositoryChanges").EnumerateArray().Select(ReadChange).ToList(),
        Tests: OptionalStrings(root, "tests"),
        Risks: OptionalStrings(root, "risks"),
        OpenQuestions: OptionalStrings(root, "openQuestions"),
        Budget: new BudgetLimits(0m, TimeSpan.Zero, 0, 0),
        Providers: OptionalStrings(root, "providers"),
        P1Checks: OptionalStrings(root, "p1Checks"));

    private static PlannedRepositoryChange ReadChange(JsonElement element)
    {
        if (!Enum.TryParse<ChangeIntent>(element.GetProperty("intent").GetString(), ignoreCase: true, out var intent))
            throw new InvalidOperationException("repositoryChanges[].intent");
        return new PlannedRepositoryChange(
            element.GetProperty("repositoryId").GetGuid(),
            element.GetProperty("repositoryName").GetString() ?? throw new InvalidOperationException("repositoryName"),
            intent,
            element.GetProperty("baselineCommit").GetString() ?? throw new InvalidOperationException("baselineCommit"));
    }

    private static List<string> OptionalStrings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [];

    private static IReadOnlyList<ProposedQuestion> ReadQuestions(JsonElement root)
    {
        if (!root.TryGetProperty("questions", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray()
            .Select(q => new ProposedQuestion(
                q.GetProperty("question").GetString() ?? throw new InvalidOperationException("questions[].question"),
                q.TryGetProperty("domain", out var domain) ? domain.GetString() : null))
            .ToList();
    }
}
