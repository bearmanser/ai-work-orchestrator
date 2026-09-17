namespace Orchestrator.Application.Steps;

/// <summary>
/// The single convention for orchestrator work branches, shared by the
/// implementation step (which creates them) and PR creation (which pushes and
/// looks them up). One format in one place so the two can never disagree.
/// </summary>
public static class GitBranchNaming
{
    public static string WorkBranchFor(Guid taskId) => $"orchestrator/{taskId}";
}
