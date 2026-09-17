using LibGit2Sharp;
using Npgsql;
using Orchestrator.Adapters.Agent.Providers;
using Orchestrator.Application.Ports;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// The acceptance suite's stand-in for a real coding agent provider. Wraps the
/// real <see cref="ReplayAgentProvider"/> (Task 28) with two things production's
/// own registration cannot give it:
///
/// 1. A deterministic scenario name. Production selects the replay scenario by
///    <see cref="AgentRequest.OperationId"/>, which planning and implementation
///    both mint fresh (<c>Guid.CreateVersion7()</c>) on every call - there is no
///    way to pre-record a fixture file under a name nobody can predict. Here the
///    scenario is chosen from the request's stage and the presence of a marker
///    the acceptance tests put in the task objective, which is exactly the kind
///    of test-only wiring the brief expects ("you will need to build real
///    infrastructure to prove them").
/// 2. A commit. The real coding agents run inside a sandbox and commit their own
///    changes as part of doing the work; <see cref="ReplayAgentProvider"/> only
///    writes files to disk (proven by its own unit tests) and never commits.
///    Without a commit, <c>LibGit2Workspace.CaptureDiffAsync</c> (which diffs
///    the branch tip against the baseline commit, not the working tree) would
///    see zero changed files and pull-request creation would have nothing to
///    open a PR for. Committing here reproduces exactly what the sandboxed
///    agent would have done, so the rest of the pipeline (diff capture, push,
///    PR creation) runs unmodified and for real.
///
/// Every invocation is also recorded in a dedicated Postgres table
/// (<c>acceptance_agent_invocations</c>) that is not part of the production
/// schema, so <c>AcceptanceHost.AgentInvocationsForAsync</c> can prove "exactly
/// once" across a genuine process restart without adding test-only columns to
/// any real table.
/// </summary>
public sealed class AcceptanceReplayAgent(string replaysDirectory, string connectionString) : ICodingAgent
{
    private static readonly Signature CommitSignature = new(
        "replay-agent", "replay-agent@example.com", DateTimeOffset.UtcNow);

    private readonly ReplayAgentProvider _inner = new(replaysDirectory, SelectScenario);

    public string Name => "replay";

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        await RecordInvocationAsync(request, ct);

        var result = await _inner.ExecuteAsync(request, ct);

        if (request.Stage == AgentStage.Implementation
            && result.Status == AgentStatus.Succeeded
            && !string.IsNullOrEmpty(request.Workspace.Path)
            && Repository.IsValid(request.Workspace.Path))
        {
            CommitIfDirty(request.Workspace.Path);
        }

        return result;
    }

    private static string SelectScenario(AgentRequest request) => request.Stage switch
    {
        AgentStage.Planning when request.Instructions.Objective.Contains(
            "[BLOCKING_QUESTION]", StringComparison.Ordinal) => "planning-question",
        AgentStage.Planning => "planning",
        AgentStage.Implementation => "implementation",
        _ => throw new InvalidOperationException(
            $"The acceptance suite has no replay scenario for stage '{request.Stage}'."),
    };

    private static void CommitIfDirty(string workspacePath)
    {
        using var repo = new Repository(workspacePath);
        Commands.Stage(repo, "*");
        var status = repo.RetrieveStatus();
        if (!status.IsDirty)
            return; // Nothing changed: this repository is a ReferenceOnly target in the plan.

        repo.Commit("Replay agent implementation", CommitSignature, CommitSignature);
    }

    private async Task RecordInvocationAsync(AgentRequest request, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS orchestrator.acceptance_agent_invocations (
                id uuid PRIMARY KEY,
                task_id uuid NOT NULL,
                stage text NOT NULL,
                operation_id text NOT NULL,
                occurred_at timestamptz NOT NULL
            );
            INSERT INTO orchestrator.acceptance_agent_invocations (id, task_id, stage, operation_id, occurred_at)
            VALUES (@id, @taskId, @stage, @operationId, now());
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("taskId", request.TaskId);
        command.Parameters.AddWithValue("stage", request.Stage.ToString());
        command.Parameters.AddWithValue("operationId", request.OperationId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
