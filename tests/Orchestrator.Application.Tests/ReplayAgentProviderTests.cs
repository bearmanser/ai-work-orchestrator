using System.Text.Json;
using Orchestrator.Adapters.Agent.Providers;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Budgets;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class ReplayAgentProviderTests : IDisposable
{
    private readonly string _replaysDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-replays-" + Guid.NewGuid())).FullName;

    private readonly string _workspaceDirectory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "replay-workspace-" + Guid.NewGuid())).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_replaysDirectory))
            Directory.Delete(_replaysDirectory, recursive: true);
        if (Directory.Exists(_workspaceDirectory))
            Directory.Delete(_workspaceDirectory, recursive: true);
    }

    [Fact]
    public async Task Execute_KnownScenario_ReturnsRecordedResultAndAppliesFileMutations()
    {
        var resultJson = JsonSerializer.Serialize(new
        {
            Status = "Succeeded",
            Provider = "replay",
            Model = "replay-model",
            BaselineCommit = "abc",
            ResultCommit = "def",
            DiffArtifactId = (string?)null,
            ClaimedChecks = new[] { new { Command = "dotnet test", ClaimedPassed = true } },
            UnresolvedQuestions = Array.Empty<string>(),
            Limitations = Array.Empty<string>(),
            Usage = new { CostUsd = 1.5m, Duration = "00:01:00", InputTokens = 10, OutputTokens = 5 },
            FailureDetail = (string?)null,
        });
        await File.WriteAllTextAsync(
            Path.Combine(_replaysDirectory, "success.json"),
            JsonSerializer.Serialize(new
            {
                Result = JsonSerializer.Deserialize<JsonElement>(resultJson),
                Files = new[] { new { Path = "src/New.cs", Content = "public class New;" } },
            }));

        var provider = new ReplayAgentProvider(_replaysDirectory, _ => "success");
        var result = await provider.ExecuteAsync(Request(), default);

        result.Status.ShouldBe(AgentStatus.Succeeded);
        result.Provider.ShouldBe("replay");
        result.ClaimedChecks.Single().Command.ShouldBe("dotnet test");
        (await File.ReadAllTextAsync(Path.Combine(_workspaceDirectory, "src", "New.cs")))
            .ShouldBe("public class New;");
    }

    [Fact]
    public async Task Execute_UnknownScenario_Throws() =>
        await Should.ThrowAsync<FileNotFoundException>(() =>
            new ReplayAgentProvider(_replaysDirectory, _ => "missing").ExecuteAsync(Request(), default));

    private AgentRequest Request() => new(
        TaskId: Guid.CreateVersion7(),
        OperationId: "op-1",
        Stage: AgentStage.Implementation,
        Instructions: new TrustedInstructions("objective", ["criterion"], [], []),
        Evidence: new EvidenceBundle([], [], []),
        Workspace: new WorkspaceHandle("ws-1", _workspaceDirectory, "abc", "main", "work"),
        ProviderPolicy: new ProviderPolicy(["replay"], false),
        Budget: new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3),
        AuthorizationRef: "auth-1");
}
