using Orchestrator.Application.Agents;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Agent.Providers;

/// <summary>
/// Runs the Claude CLI inside the sandbox as an implementation of <see cref="ICodingAgent"/>.
/// </summary>
/// <remarks>
/// Deliberately self-contained rather than built on a shared base class: it renders the prompt,
/// resolves the API key, builds the <see cref="SandboxRequest"/>, runs it, and parses the result,
/// all here. A shared base with the concurrently-developed Codex provider is left for a follow-up
/// once both providers exist and their actual common surface is visible.
/// </remarks>
public sealed class ClaudeCliProvider(
    SandboxedProcessRunner runner,
    ISecretStore secretStore,
    ClaudeCliOptions options) : ICodingAgent
{
    public string Name => "claude";

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        if (!string.Equals(options.PromptVia, "Stdin", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"ClaudeCliProvider only supports delivering the prompt via stdin; " +
                $"AgentProviders:claude:PromptVia was '{options.PromptVia}'.");
        }

        var prompt = PromptRenderer.Render(request);
        var apiKey = await secretStore.GetAsync(new SecretRef(options.ApiKeySecretRef), ct);

        var command = new List<string>(options.Command.Count + options.Arguments.Count);
        command.AddRange(options.Command);
        command.AddRange(options.Arguments);

        var sandboxRequest = new SandboxRequest(
            Image: options.Image,
            Command: command,
            WorkspacePath: request.Workspace.Path,
            Environment: new Dictionary<string, string> { [options.ApiKeyEnvironmentVariable] = apiKey },
            AllowedHosts: options.AllowedHosts,
            Timeout: options.Timeout,
            StdinContent: prompt);

        var processResult = await runner.RunAsync(sandboxRequest, ct);

        if (processResult.TimedOut)
        {
            return new AgentResult(
                Status: AgentStatus.Partial,
                Provider: Name,
                Model: options.Model,
                BaselineCommit: request.Workspace.BaselineCommit,
                ResultCommit: null,
                DiffArtifactId: null,
                ClaimedChecks: [],
                UnresolvedQuestions: [],
                Limitations: [],
                Usage: new AgentUsage(0m, processResult.Duration),
                FailureDetail: $"Sandboxed run exceeded its timeout of {options.Timeout}.");
        }

        return ClaudeResultParser.Parse(processResult.Stdout, request.Workspace.BaselineCommit, options.Model);
    }
}
