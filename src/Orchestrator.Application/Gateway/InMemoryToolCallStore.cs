using System.Text.Json;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Operations;

namespace Orchestrator.Application.Gateway;

/// <summary>
/// In-process tool-call store. Correct for idempotency within one host lifetime;
/// a durable implementation (so Unknown outcomes survive restarts for the
/// reconciler) is still open.
/// </summary>
public sealed class InMemoryToolCallStore : IToolCallStore
{
    private readonly Dictionary<string, ToolCallRecord> _records = [];
    private readonly object _gate = new();

    public Task<ToolCallRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_gate)
        {
            _records.TryGetValue(idempotencyKey, out var record);
            return Task.FromResult(record);
        }
    }

    public Task<ToolCallRecord> RecordIntentAsync(ToolCall call, PolicyDecision decision, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(call.IdempotencyKey, out var existing))
            {
                switch (existing.Outcome)
                {
                    case ToolCallOutcome.Unknown:
                        throw new UnreconciledOperationException(call.IdempotencyKey);
                    case ToolCallOutcome.Succeeded:
                        throw new OperationAlreadySucceededException(call.IdempotencyKey);
                    case ToolCallOutcome.Failed:
                        existing.ResetForRetry(now);
                        return Task.FromResult(existing);
                }
            }

            var record = new ToolCallRecord(
                Guid.CreateVersion7(),
                call.TaskId,
                call.StepId,
                call.Action,
                call.OperationId,
                call.IdempotencyKey,
                ToolCallOutcome.Unknown,
                decision,
                null,
                now,
                null);
            _records[call.IdempotencyKey] = record;
            return Task.FromResult(record);
        }
    }

    public Task RecordSuccessAsync<TResult>(Guid recordId, TResult? value, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var existing = _records.Values.FirstOrDefault(r => r.Id == recordId);
            existing?.Complete(
                ToolCallOutcome.Succeeded,
                value is null ? null : JsonSerializer.Serialize(value),
                now);

            return Task.CompletedTask;
        }
    }

    public TResult? Deserialize<TResult>(ToolCallRecord record) =>
        record.ResultJson is null ? default : JsonSerializer.Deserialize<TResult>(record.ResultJson);

    public Task<bool> HasUnknownOutcomesAsync(Guid taskId, CancellationToken ct)
    {
        lock (_gate)
        {
            var hasUnknown = _records.Values.Any(r => r.TaskId == taskId && r.Outcome == ToolCallOutcome.Unknown);
            return Task.FromResult(hasUnknown);
        }
    }

    public Task<IReadOnlyList<ToolCallRecord>> FindUnknownAsync(Guid taskId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<ToolCallRecord> unknown = _records.Values
                .Where(r => r.TaskId == taskId && r.Outcome == ToolCallOutcome.Unknown)
                .ToList();
            return Task.FromResult(unknown);
        }
    }

    public Task ResolveAsync(Guid recordId, ToolCallOutcome outcome, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var record = _records.Values.FirstOrDefault(r => r.Id == recordId);
            record?.Complete(outcome, record.ResultJson, now);
            return Task.CompletedTask;
        }
    }
}
