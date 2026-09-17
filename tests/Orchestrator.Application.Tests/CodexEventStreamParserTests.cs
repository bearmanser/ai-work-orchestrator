using Orchestrator.Adapters.Agent.Providers;
using Orchestrator.Application.Ports;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class CodexEventStreamParserTests
{
    private const string Stream = """
    {"type":"session.created","session_id":"s-1"}
    {"type":"item.completed","item":{"type":"command_execution","command":"dotnet test","exit_code":0}}
    {"type":"item.completed","item":{"type":"agent_message","text":"Added the endpoint."}}
    {"type":"turn.completed","usage":{"input_tokens":90000,"output_tokens":3000}}
    """;

    [Fact]
    public void Parse_CompletedStream_IsSucceeded() =>
        CodexEventStreamParser.Parse(Stream, "abc", "gpt-5-codex").Status.ShouldBe(AgentStatus.Succeeded);

    [Fact]
    public void Parse_CommandExecutionEvents_BecomeClaimedChecks()
    {
        var result = CodexEventStreamParser.Parse(Stream, "abc", "gpt-5-codex");
        result.ClaimedChecks.ShouldContain(c => c.Command == "dotnet test" && c.ClaimedPassed);
    }

    [Fact]
    public void Parse_NonZeroExitCommand_IsAClaimedFailure()
    {
        var stream = """
            {"type":"item.completed","item":{"type":"command_execution","command":"dotnet test","exit_code":1}}
            {"type":"turn.completed","usage":{}}
            """;
        CodexEventStreamParser.Parse(stream, "abc", "gpt-5-codex")
            .ClaimedChecks.Single().ClaimedPassed.ShouldBeFalse();
    }

    [Fact]
    public void Parse_TurnFailedEvent_IsFailed()
    {
        var stream = """{"type":"turn.failed","error":{"message":"rate limited"}}""";
        var result = CodexEventStreamParser.Parse(stream, "abc", "gpt-5-codex");

        result.Status.ShouldBe(AgentStatus.Failed);
        result.FailureDetail.ShouldNotBeNull().ShouldContain("rate limited");
    }

    [Fact]
    public void Parse_StreamEndingWithoutTurnCompleted_IsPartial()
    {
        var stream = """{"type":"session.created","session_id":"s-1"}""";
        CodexEventStreamParser.Parse(stream, "abc", "gpt-5-codex").Status.ShouldBe(AgentStatus.Partial);
    }

    [Fact]
    public void Parse_UnknownEventTypes_AreIgnoredNotFatal()
    {
        var stream = """
            {"type":"some.future.event","payload":{}}
            {"type":"turn.completed","usage":{}}
            """;
        Should.NotThrow(() => CodexEventStreamParser.Parse(stream, "abc", "gpt-5-codex"));
    }

    [Fact]
    public void Parse_MalformedLineAmongValidOnes_IsSkipped()
    {
        var stream = "not json\n" + """{"type":"turn.completed","usage":{}}""";
        CodexEventStreamParser.Parse(stream, "abc", "gpt-5-codex").Status.ShouldBe(AgentStatus.Succeeded);
    }
}
