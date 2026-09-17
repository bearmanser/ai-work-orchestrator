using Orchestrator.Application.Agents;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Budgets;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class AgentRouterTests
{
    [Fact]
    public async Task Execute_FirstProviderSucceeds_DoesNotCallTheSecond()
    {
        var setup = Setup();
        var claude = setup.Provider("claude", Success());
        var codex = setup.Provider("codex", Success());
        await setup.Router([claude, codex]).ExecuteAsync(
            setup.RequestWithPolicy(["claude", "codex"], fallbackAllowed: true), default);

        codex.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task Execute_TransientFailureWithFallbackAllowed_UsesTheSecondProvider()
    {
        var setup = Setup();
        var claude = setup.Provider("claude", throws: new HttpRequestException("503"));
        var codex = setup.Provider("codex", Success());

        var result = await setup.Router([claude, codex]).ExecuteAsync(
            setup.RequestWithPolicy(["claude", "codex"], fallbackAllowed: true), default);

        result.Provider.ShouldBe("codex");
    }

    [Fact]
    public async Task Execute_TransientFailureWithFallbackDisallowed_Fails()
    {
        var setup = Setup();
        var claude = setup.Provider("claude", throws: new HttpRequestException("503"));
        var codex = setup.Provider("codex", Success());

        var result = await setup.Router([claude, codex]).ExecuteAsync(
            setup.RequestWithPolicy(["claude", "codex"], fallbackAllowed: false), default);

        result.Status.ShouldBe(AgentStatus.Failed);
        codex.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task Execute_PolicyDenial_NeverFallsBack()
    {
        // Falling back after a denial would be using provider choice to defeat policy.
        var setup = Setup();
        var claude = setup.Provider("claude", throws: new PolicyDeniedException("stage not authorized"));
        var codex = setup.Provider("codex", Success());

        var result = await setup.Router([claude, codex]).ExecuteAsync(
            setup.RequestWithPolicy(["claude", "codex"], fallbackAllowed: true), default);

        result.Status.ShouldBe(AgentStatus.Failed);
        codex.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task Execute_ImplementationFailure_NeverFallsBack()
    {
        // The agent worked and the change did not pass. A different model is not the remedy.
        var setup = Setup();
        var claude = setup.Provider("claude", Failed("tests still red"));
        var codex = setup.Provider("codex", Success());

        await setup.Router([claude, codex]).ExecuteAsync(
            setup.RequestWithPolicy(["claude", "codex"], fallbackAllowed: true), default);

        codex.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task Execute_ProviderNotInPolicy_IsNeverSelected()
    {
        var setup = Setup();
        var gemini = setup.Provider("gemini", Success());
        var result = await setup.Router([gemini]).ExecuteAsync(
            setup.RequestWithPolicy(["claude"], fallbackAllowed: true), default);

        result.Status.ShouldBe(AgentStatus.Failed);
        gemini.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task Execute_TransientFailure_RetriesSameProviderBeforeFallingBack()
    {
        var setup = Setup();
        var claude = setup.ProviderFailingTimes("claude", times: 2, thenSucceeding: true);
        var result = await setup.Router([claude]).ExecuteAsync(
            setup.RequestWithPolicy(["claude"], fallbackAllowed: false), default);

        result.Status.ShouldBe(AgentStatus.Succeeded);
        claude.Invocations.ShouldBe(3);
    }

    [Fact]
    public async Task Execute_ExhaustedRetries_ReportsTheLastFailure()
    {
        var setup = Setup();
        var claude = setup.ProviderFailingTimes("claude", times: 99, thenSucceeding: false);
        var result = await setup.Router([claude]).ExecuteAsync(
            setup.RequestWithPolicy(["claude"], fallbackAllowed: false), default);

        result.Status.ShouldBe(AgentStatus.Failed);
        claude.Invocations.ShouldBe(3); // bounded
    }

    [Fact]
    public async Task Execute_TransientRetries_WaitTwoThenEightSeconds()
    {
        var setup = Setup();
        var claude = setup.ProviderFailingTimes("claude", times: 99, thenSucceeding: false);
        await setup.Router([claude]).ExecuteAsync(
            setup.RequestWithPolicy(["claude"], fallbackAllowed: false), default);

        setup.Delays.ShouldBe([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)]);
    }

    [Fact]
    public async Task Execute_AuthenticationFailure_DoesNotRetrySameProvider()
    {
        var setup = Setup();
        var claude = setup.Provider("claude",
            throws: new HttpRequestException(null, null, System.Net.HttpStatusCode.Unauthorized));
        var codex = setup.Provider("codex", Success());

        var result = await setup.Router([claude, codex]).ExecuteAsync(
            setup.RequestWithPolicy(["claude", "codex"], fallbackAllowed: true), default);

        claude.Invocations.ShouldBe(1);
        result.Provider.ShouldBe("codex");
    }

    [Fact]
    public async Task Execute_FallbackAfterPartialWork_CapturesTheDiffAsAnArtifact()
    {
        var setup = Setup();
        var claude = setup.Provider("claude", throws: new HttpRequestException("503"));
        var codex = setup.Provider("codex", Success());

        await setup.Router([claude, codex]).ExecuteAsync(
            setup.RequestWithPolicy(["claude", "codex"], fallbackAllowed: true), default);

        setup.Artifacts.Stored.ShouldContain(s => s.Contains("diff --git"));
    }

    private static RouterSetup Setup() => new();

    private static AgentResult Success() => new(
        AgentStatus.Succeeded, "claude", "test-model", "abc", "def", null,
        [], [], [], new AgentUsage(1m, TimeSpan.FromSeconds(1)), null);

    private static AgentResult Failed(string detail) => new(
        AgentStatus.Failed, "claude", "test-model", "abc", null, null,
        [], [], [], new AgentUsage(1m, TimeSpan.FromSeconds(1)), detail);

    private sealed class RouterSetup
    {
        public List<TimeSpan> Delays { get; } = [];
        public FakeArtifactStore Artifacts { get; } = new();

        public FakeAgent Provider(string name, AgentResult result) =>
            new(name, _ => Task.FromResult(result));

        public FakeAgent Provider(string name, Exception throws) =>
            new(name, _ => Task.FromException<AgentResult>(throws));

        public FakeAgent ProviderFailingTimes(string name, int times, bool thenSucceeding)
        {
            var remaining = times;
            return new(name, _ =>
            {
                if (remaining-- > 0)
                    return Task.FromException<AgentResult>(new HttpRequestException("503"));
                return thenSucceeding
                    ? Task.FromResult(Success())
                    : Task.FromException<AgentResult>(new HttpRequestException("503"));
            });
        }

        public AgentRouter Router(IReadOnlyList<ICodingAgent> providers) => new(
            providers,
            new FakeGitWorkspaces(),
            Artifacts,
            (delay, _) =>
            {
                Delays.Add(delay);
                return Task.CompletedTask;
            });

        public AgentRequest RequestWithPolicy(IReadOnlyList<string> allowed, bool fallbackAllowed) => new(
            TaskId: Guid.CreateVersion7(),
            OperationId: "op-1",
            Stage: AgentStage.Implementation,
            Instructions: new TrustedInstructions("objective", ["criterion"], [], []),
            Evidence: new EvidenceBundle([], [], []),
            Workspace: new WorkspaceHandle("ws-1", Path.GetTempPath(), "abc", "main", "work"),
            ProviderPolicy: new ProviderPolicy(allowed, fallbackAllowed),
            Budget: new BudgetLimits(25m, TimeSpan.FromHours(4), 10, 3),
            AuthorizationRef: "auth-1");
    }

    private sealed class FakeAgent(string name, Func<AgentRequest, Task<AgentResult>> behavior) : ICodingAgent
    {
        public string Name { get; } = name;
        public int Invocations { get; private set; }

        public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
        {
            Invocations++;
            return behavior(request);
        }
    }

    private sealed class FakeGitWorkspaces : IGitWorkspace
    {
        public Task<WorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GitDiff> CaptureDiffAsync(string workspaceId, CancellationToken ct) =>
            Task.FromResult(new GitDiff("abc", "abc", [], "diff --git a/x b/x"));

        public Task<string> PushAsync(string workspaceId, SecretRef credentialRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> HasBaselineDriftedAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DisposeAsync(string workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        public List<string> Stored { get; } = [];

        public Task<string> PutAsync(ArtifactDescriptor descriptor, Stream content, CancellationToken ct)
        {
            using var reader = new StreamReader(content);
            Stored.Add(reader.ReadToEnd());
            return Task.FromResult(Guid.CreateVersion7().ToString("N"));
        }

        public Task<Stream> OpenAsync(string artifactId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
