using Orchestrator.Application.Agents;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Agent.Providers;

/// <summary>
/// Configuration for <see cref="CodexCliProvider"/>, bound from the <c>AgentProviders:codex</c>
/// configuration section. The command line and its arguments are configuration rather than
/// hard-coded because the Codex CLI's own surface changes faster than this service does.
/// </summary>
/// <param name="Image">The sandbox Docker image to run the CLI inside.</param>
/// <param name="Command">The executable and leading arguments (e.g. <c>["codex"]</c>).</param>
/// <param name="Arguments">Additional CLI arguments appended after <paramref name="Command"/>.</param>
/// <param name="PromptVia">How the rendered prompt is delivered to the CLI. Only <c>"Stdin"</c> is supported.</param>
/// <param name="ApiKeySecretRef">The <see cref="SecretRef"/> key used to resolve the OpenAI API key.</param>
/// <param name="ApiKeyEnvironmentVariable">The environment variable the resolved API key is injected as inside the sandbox.</param>
/// <param name="Model">The model name passed through to <see cref="AgentResult.Model"/>.</param>
/// <param name="AllowedHosts">Hosts this run is authorized to reach (see <see cref="SandboxRequest.AllowedHosts"/>).</param>
public sealed record CodexCliOptions(
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyList<string> Arguments,
    string PromptVia,
    string ApiKeySecretRef,
    string ApiKeyEnvironmentVariable,
    string Model,
    IReadOnlyList<string>? AllowedHosts = null);

/// <summary>
/// Runs the Codex CLI inside the sandbox as a coding agent.
/// </summary>
/// <remarks>
/// Deliberately self-contained rather than sharing a base class with the Claude provider: the
/// two are being built concurrently on separate branches, and extracting their common surface
/// (sandbox invocation, secret resolution) is left for a follow-up once both providers exist and
/// that surface is visible from two real implementations rather than guessed in advance.
/// </remarks>
public sealed class CodexCliProvider(
    CodexCliOptions options,
    SandboxedProcessRunner runner,
    ISecretStore secretStore) : ICodingAgent
{
    public string Name => "codex";

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        if (options.PromptVia != "Stdin")
        {
            throw new NotSupportedException(
                $"CodexCliProvider only supports PromptVia \"Stdin\", got \"{options.PromptVia}\".");
        }

        var prompt = PromptRenderer.Render(request);
        var apiKey = await secretStore.GetAsync(new SecretRef(options.ApiKeySecretRef), ct);

        var sandboxRequest = new SandboxRequest(
            Image: options.Image,
            Command: [.. options.Command, .. options.Arguments],
            WorkspacePath: request.Workspace.Path,
            Environment: new Dictionary<string, string> { [options.ApiKeyEnvironmentVariable] = apiKey },
            AllowedHosts: options.AllowedHosts ?? [],
            Timeout: request.Budget.MaxWallClock,
            StdinContent: prompt);

        var processResult = await runner.RunAsync(sandboxRequest, ct);

        var parsed = CodexEventStreamParser.Parse(processResult.Stdout, request.Workspace.BaselineCommit, options.Model);

        // The fold itself has no notion of wall-clock time; splice in what the sandbox actually
        // measured rather than have the parser guess at it.
        return parsed with { Usage = parsed.Usage with { Duration = processResult.Duration } };
    }
}
