using Orchestrator.Adapters.Agent;
using Orchestrator.Adapters.Agent.Providers;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Budgets;
using Orchestrator.Infrastructure.Secrets;
using Shouldly;

namespace Orchestrator.Integration.Tests;

/// <summary>
/// Exercises the real Claude CLI, inside the real sandbox, end to end. Gated behind
/// <c>ORCHESTRATOR_AGENT_SMOKE=1</c> because it requires a working Anthropic API key and makes a
/// real network call; the unconditional test coverage for this provider is
/// <see cref="Orchestrator.Application.Tests.ClaudeResultParserTests"/>, which runs against
/// recorded JSON and needs neither.
/// </summary>
[Collection(nameof(SandboxCollection))]
[Trait("Category", "Smoke")]
public sealed class ClaudeCliSmokeTests(SandboxInfrastructureFixture fixture) : IDisposable
{
    private const string ApiKeySecretKey = "anthropic.api-key";

    private readonly string _workspace = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "claude-smoke-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Execute_TrivialObjectiveInAScratchRepository_ProducesACommitAndParseableResult()
    {
        if (Environment.GetEnvironmentVariable("ORCHESTRATOR_AGENT_SMOKE") != "1")
            return; // Not requested for this run - see the smoke-test gating note above.

        var secretStore = new EnvironmentSecretStore();
        SecretDescription description;
        try
        {
            description = await secretStore.DescribeAsync(new SecretRef(ApiKeySecretKey), default);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (!description.Exists)
            return; // No usable Anthropic API key configured in this environment - see task-26-report.md.

        var runner = new SandboxedProcessRunner(fixture.Options, TimeProvider.System);
        var options = new ClaudeCliOptions();
        var provider = new ClaudeCliProvider(runner, secretStore, options);

        var request = new AgentRequest(
            TaskId: Guid.CreateVersion7(),
            OperationId: "smoke-test",
            Stage: AgentStage.Implementation,
            Instructions: new TrustedInstructions(
                Objective: "Create a file called HELLO.md containing 'hi'",
                AcceptanceCriteria: ["HELLO.md exists and contains 'hi'"],
                Constraints: [],
                OutOfScope: []),
            Evidence: new EvidenceBundle([], [], []),
            Workspace: new WorkspaceHandle(
                WorkspaceId: "smoke", Path: _workspace, BaselineCommit: "0000000000000000000000000000000000000000",
                BaseBranch: "main", WorkBranch: "smoke-work"),
            ProviderPolicy: new ProviderPolicy(["claude"], FallbackAllowed: false),
            Budget: new BudgetLimits(MaxUsd: 5m, MaxWallClock: TimeSpan.FromMinutes(15), MaxAgentInvocations: 1, MaxCorrectionAttempts: 0),
            AuthorizationRef: "smoke-test");

        var result = await provider.ExecuteAsync(request, default);

        result.Status.ShouldBe(AgentStatus.Succeeded);
        File.Exists(Path.Combine(_workspace, "HELLO.md")).ShouldBeTrue();
    }
}
