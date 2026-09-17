using System.Text.Json;
using System.Text.Json.Serialization;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Agent.Providers;

/// <summary>
/// Deterministic stand-in for a real coding agent. Reads a recorded
/// <see cref="AgentResult"/> plus a set of file mutations from
/// <c>{replaysDirectory}/{scenario}.json</c> and applies the files to the
/// request's workspace. This is what makes the implementation/verification
/// steps and the acceptance suite deterministic.
/// </summary>
public sealed class ReplayAgentProvider(
    string replaysDirectory,
    Func<AgentRequest, string>? scenarioSelector = null) : ICodingAgent
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Name => "replay";

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var scenario = (scenarioSelector ?? (r => r.OperationId))(request);
        var json = await File.ReadAllTextAsync(Path.Combine(replaysDirectory, scenario + ".json"), ct);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("Files", out var files))
        {
            foreach (var file in files.EnumerateArray())
            {
                var relativePath = file.GetProperty("Path").GetString()
                    ?? throw new InvalidDataException($"Replay scenario '{scenario}' has a file without a path.");
                var content = file.GetProperty("Content").GetString() ?? string.Empty;

                var workspaceRoot = Path.GetFullPath(request.Workspace.Path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
                if (fullPath != workspaceRoot
                    && !fullPath.StartsWith(workspaceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException(
                        $"Replay scenario '{scenario}' escapes the workspace: '{relativePath}'.");

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, content, ct);
            }
        }

        return root.GetProperty("Result").Deserialize<AgentResult>(SerializerOptions)
            ?? throw new InvalidDataException($"Replay scenario '{scenario}' has no result.");
    }
}
