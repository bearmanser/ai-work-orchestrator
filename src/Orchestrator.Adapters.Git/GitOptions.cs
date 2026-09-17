namespace Orchestrator.Adapters.Git;

/// <summary>Filesystem locations for Git operations, bound from configuration.</summary>
public sealed class GitOptions
{
    public string Root { get; set; } = string.Empty;
}
