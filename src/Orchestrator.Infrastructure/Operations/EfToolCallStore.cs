using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Gateway;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Operations;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Operations;

/// <summary>
/// EF-backed tool-call store. Unlike <see cref="InMemoryToolCallStore"/>, an
/// <see cref="ToolCallOutcome.Unknown"/> record written here survives a process
/// restart - the retry gate it enforces does too, which is the point: a restart is
/// often exactly what caused the uncertainty in the first place.
/// </summary>
public sealed class EfToolCallStore(OrchestratorDbContext db) : IToolCallStore
{
    public async Task<ToolCallRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct) =>
        await db.ToolCallRecords.SingleOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, ct);

    public async Task<ToolCallRecord> RecordIntentAsync(
        ToolCall call, PolicyDecision decision, DateTimeOffset now, CancellationToken ct)
    {
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

        db.ToolCallRecords.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task RecordSuccessAsync<TResult>(Guid recordId, TResult? value, DateTimeOffset now, CancellationToken ct)
    {
        var record = await db.ToolCallRecords.SingleOrDefaultAsync(r => r.Id == recordId, ct);
        record?.Complete(ToolCallOutcome.Succeeded, value is null ? null : JsonSerializer.Serialize(value), now);
        await db.SaveChangesAsync(ct);
    }

    public TResult? Deserialize<TResult>(ToolCallRecord record) =>
        record.ResultJson is null ? default : JsonSerializer.Deserialize<TResult>(record.ResultJson);

    public async Task<bool> HasUnknownOutcomesAsync(Guid taskId, CancellationToken ct) =>
        await db.ToolCallRecords.AnyAsync(r => r.TaskId == taskId && r.Outcome == ToolCallOutcome.Unknown, ct);

    public async Task<IReadOnlyList<ToolCallRecord>> FindUnknownAsync(Guid taskId, CancellationToken ct) =>
        await db.ToolCallRecords
            .Where(r => r.TaskId == taskId && r.Outcome == ToolCallOutcome.Unknown)
            .ToListAsync(ct);

    public async Task ResolveAsync(Guid recordId, ToolCallOutcome outcome, DateTimeOffset now, CancellationToken ct)
    {
        var record = await db.ToolCallRecords.SingleOrDefaultAsync(r => r.Id == recordId, ct);
        record?.Complete(outcome, record.ResultJson, now);
        await db.SaveChangesAsync(ct);
    }
}
