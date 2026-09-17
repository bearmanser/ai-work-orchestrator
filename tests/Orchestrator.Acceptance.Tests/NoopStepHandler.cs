using Npgsql;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Acceptance.Tests;

/// <summary>
/// A trivial, always-succeeding <see cref="IStepHandler"/> registered only in
/// the acceptance host, under a capability name ("acceptance.noop") no
/// production step ever uses. The concurrency suite's lease-racing test cares
/// about the lease mechanism itself, not about any particular business
/// capability's side effects, so this isolates that proof from the setup cost
/// (envelope, budget, workspace, ...) every real handler needs. Every
/// invocation is recorded in the same dedicated table
/// <see cref="AcceptanceReplayAgent"/> uses for agent invocations, so a test
/// can assert exactly how many of several racing callers actually ran the
/// handler body.
/// </summary>
public sealed class NoopStepHandler(string connectionString) : IStepHandler
{
    public string Capability => "acceptance.noop";

    public async Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS orchestrator.acceptance_noop_invocations (
                id uuid PRIMARY KEY,
                step_id uuid NOT NULL,
                occurred_at timestamptz NOT NULL
            );
            INSERT INTO orchestrator.acceptance_noop_invocations (id, step_id, occurred_at)
            VALUES (@id, @stepId, now());
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("stepId", context.StepId);
        await command.ExecuteNonQueryAsync(ct);

        return new StepOutcome(null, "noop-output", null);
    }
}
