using Orchestrator.Adapters.Agent;
using Orchestrator.Adapters.Agent.Providers;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Budgets;
using Orchestrator.Infrastructure.Secrets;
using Shouldly;

namespace Orchestrator.Integration.Tests;

/// <summary>
/// Exercises <see cref="CodexCliProvider"/> against the real Codex CLI, inside the real sandbox,
/// with a real OpenAI API key. Everything else in this task is verified against recorded JSONL
/// (see <c>CodexEventStreamParserTests</c>); this is the one place the plan depends on an
/// external tool's actual surface, so it is skipped unless explicitly opted into.
/// </summary>
[Collection(nameof(SandboxCollection))]
[Trait("Category", "Smoke")]
public sealed class CodexCliSmokeTests(SandboxInfrastructureFixture fixture) : IDisposable
{
    private const string ApiKeySecretKey = "openai.api-key";

    private readonly string _workspace = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "codex-smoke-" + Guid.NewGuid().ToString("N"))).FullName;

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
    public async Task Execute_TrivialObjectiveInAScratchRepository_ProducesAParseableResult()
    {
        if (Environment.GetEnvironmentVariable("ORCHESTRATOR_AGENT_SMOKE") != "1")
            return; // Opt-in only: requires a real OpenAI API key and the real Codex CLI.

        // No Xunit.SkippableFact package is referenced in this repo (see Directory.Packages.props),
        // so an unmet secret prerequisite fails loudly with an actionable message rather than
        // silently reporting a false pass.
        var apiKey = Environment.GetEnvironmentVariable("ORCHESTRATOR_SECRET_OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                "ORCHESTRATOR_AGENT_SMOKE=1 but ORCHESTRATOR_SECRET_OPENAI_API_KEY is not set; " +
                "cannot resolve a real OpenAI API key to run this smoke test against the real Codex CLI.");
        }

        var options = new CodexCliOptions(
            Image: "orchestrator/worker:latest",
            Command: ["codex"],
            Arguments: ["exec", "--json", "--sandbox", "workspace-write", "--cd", "/work"],
            PromptVia: "Stdin",
            ApiKeySecretRef: ApiKeySecretKey,
            ApiKeyEnvironmentVariable: "OPENAI_API_KEY",
            Model: "gpt-5-codex",
            AllowedHosts: ["api.openai.com"]);

        var provider = new CodexCliProvider(options, new SandboxedProcessRunner(fixture.Options, TimeProvider.System), new EnvironmentSecretStore());

        var request = new AgentRequest(
            TaskId: Guid.NewGuid(),
            OperationId: "smoke-test",
            Stage: AgentStage.Implementation,
            Instructions: new TrustedInstructions(
                Objective: "Create a file called HELLO.md containing 'hi'",
                AcceptanceCriteria: ["HELLO.md exists and contains 'hi'"],
                Constraints: [],
                OutOfScope: []),
            Evidence: new EvidenceBundle(Items: [], OmittedForBudget: [], OmittedForAccess: []),
            Workspace: new WorkspaceHandle(
                WorkspaceId: "smoke",
                Path: _workspace,
                BaselineCommit: "0000000000000000000000000000000000000000",
                BaseBranch: "main",
                WorkBranch: "work"),
            ProviderPolicy: new ProviderPolicy(AllowedProviders: ["codex"], FallbackAllowed: false),
            Budget: new BudgetLimits(MaxUsd: 5m, MaxWallClock: TimeSpan.FromMinutes(5), MaxAgentInvocations: 1, MaxCorrectionAttempts: 0),
            AuthorizationRef: "smoke-test");

        var result = await provider.ExecuteAsync(request, default);

        result.Status.ShouldBe(AgentStatus.Succeeded);
        File.Exists(Path.Combine(_workspace, "HELLO.md")).ShouldBeTrue();
    }
}
