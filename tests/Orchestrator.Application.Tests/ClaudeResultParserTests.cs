using Orchestrator.Adapters.Agent.Providers;
using Orchestrator.Application.Ports;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class ClaudeResultParserTests
{
    private const string SuccessJson = """
    {
      "type": "result",
      "subtype": "success",
      "is_error": false,
      "result": "Added the endpoint and a test. Ran: dotnet test (passed).",
      "session_id": "sess-1",
      "total_cost_usd": 0.4213,
      "num_turns": 9,
      "duration_ms": 84000,
      "usage": { "input_tokens": 120000, "output_tokens": 4200 }
    }
    """;

    [Fact]
    public void Parse_SuccessEnvelope_MapsStatusProviderAndUsage()
    {
        var result = ClaudeResultParser.Parse(SuccessJson, baselineCommit: "abc", model: "claude-opus-5");

        result.Status.ShouldBe(AgentStatus.Succeeded);
        result.Provider.ShouldBe("claude");
        result.Model.ShouldBe("claude-opus-5");
        result.Usage.CostUsd.ShouldBe(0.4213m);
        result.Usage.Duration.ShouldBe(TimeSpan.FromMilliseconds(84000));
    }

    [Fact]
    public void Parse_MentionedTestRun_BecomesAClaimedCheck_NotAVerifiedOne()
    {
        var result = ClaudeResultParser.Parse(SuccessJson, "abc", "claude-opus-5");
        result.ClaimedChecks.ShouldContain(c => c.Command.Contains("dotnet test"));
        // There is no property on AgentResult that can assert a check actually passed.
        typeof(AgentResult).GetProperties().ShouldNotContain(p => p.Name == "VerifiedChecks");
    }

    [Fact]
    public void Parse_ErrorEnvelope_MapsToFailedWithDetail()
    {
        var json = """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"tool denied"}""";
        var result = ClaudeResultParser.Parse(json, "abc", "claude-opus-5");

        result.Status.ShouldBe(AgentStatus.Failed);
        result.FailureDetail.ShouldNotBeNull();
        result.FailureDetail.ShouldContain("tool denied");
    }

    [Fact]
    public void Parse_MaxTurnsEnvelope_MapsToPartial()
    {
        var json = """{"type":"result","subtype":"error_max_turns","is_error":true,"result":"stopped"}""";
        ClaudeResultParser.Parse(json, "abc", "claude-opus-5").Status.ShouldBe(AgentStatus.Partial);
    }

    [Fact]
    public void Parse_UnparseableOutput_ThrowsAgentProtocolException() =>
        Should.Throw<AgentProtocolException>(
            () => ClaudeResultParser.Parse("this is not json", "abc", "claude-opus-5"));

    [Fact]
    public void Parse_TrailingNoiseBeforeJson_IsTolerated()
    {
        var noisy = "npm notice a new version is available\n" + SuccessJson;
        ClaudeResultParser.Parse(noisy, "abc", "claude-opus-5").Status.ShouldBe(AgentStatus.Succeeded);
    }
}
