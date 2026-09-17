namespace Orchestrator.Adapters.Agent.Providers;

/// <summary>
/// Configuration for <see cref="ClaudeCliProvider"/>, bound from the
/// <c>AgentProviders:claude</c> configuration section. The command line and its arguments are
/// configuration rather than hard-coded because agent CLIs change their flags faster than this
/// service will.
/// </summary>
public sealed class ClaudeCliOptions
{
    /// <summary>The Docker image the sandbox runs the CLI inside.</summary>
    public string Image { get; set; } = "orchestrator/worker:latest";

    /// <summary>The executable and any leading fixed arguments (e.g. <c>["claude"]</c>).</summary>
    public IReadOnlyList<string> Command { get; set; } = ["claude"];

    /// <summary>Flags appended after <see cref="Command"/>.</summary>
    public IReadOnlyList<string> Arguments { get; set; } =
        ["-p", "--output-format", "json", "--permission-mode", "acceptEdits"];

    /// <summary>How the rendered prompt is delivered to the CLI. Only <c>"Stdin"</c> is supported today.</summary>
    public string PromptVia { get; set; } = "Stdin";

    /// <summary>The secret store key the Anthropic API key is resolved from.</summary>
    public string ApiKeySecretRef { get; set; } = "anthropic.api-key";

    /// <summary>The environment variable name the resolved API key is injected as, inside the sandbox.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "ANTHROPIC_API_KEY";

    /// <summary>The model identifier recorded on <see cref="Application.Ports.AgentResult.Model"/>.</summary>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>Hosts the sandbox run is allowed to reach.</summary>
    public IReadOnlyList<string> AllowedHosts { get; set; } = ["api.anthropic.com"];

    /// <summary>How long a single sandboxed run is allowed before it is killed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);
}
