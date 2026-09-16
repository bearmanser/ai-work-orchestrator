using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Humans;
using Orchestrator.Domain.Humans;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>
/// Durable human-question storage plus the task/step access the question loop
/// needs. Step leases are released through EF property entries so the step
/// aggregate itself needs no lease-mutating API.
/// </summary>
public sealed class EfHumanQuestionStore(OrchestratorDbContext db)
    : IHumanQuestionStore, IQuestionTaskStore, IQuestionStepLeases
{
    public async Task AddAsync(HumanQuestion question, CancellationToken ct)
    {
        db.HumanQuestions.Add(question);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<HumanQuestion?> GetAsync(Guid id, CancellationToken ct) =>
        db.HumanQuestions.FirstOrDefaultAsync(q => q.Id == id, ct);

    async Task<HumanQuestion?> IHumanQuestionStore.GetAsync(Guid id, CancellationToken ct) =>
        await GetAsync(id, ct).ConfigureAwait(false);

    public Task<HumanQuestion?> FindByChannelThreadAsync(string channelId, string threadTs, CancellationToken ct) =>
        db.HumanQuestions.FirstOrDefaultAsync(
            q => q.ChannelId == channelId && q.ThreadTs == threadTs, ct);

    public async Task<IReadOnlyList<HumanQuestion>> ListOpenAsync(CancellationToken ct) =>
        await db.HumanQuestions
            .Where(q => q.Status == HumanQuestionStatus.Open
                || q.Status == HumanQuestionStatus.ReminderSent
                || q.Status == HumanQuestionStatus.Escalated)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task SaveAsync(HumanQuestion question, CancellationToken ct)
    {
        db.HumanQuestions.Update(question);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    async Task<WorkTask?> IQuestionTaskStore.GetAsync(Guid taskId, CancellationToken ct) =>
        await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);

    async Task IQuestionTaskStore.SaveAsync(WorkTask task, CancellationToken ct)
    {
        db.Tasks.Update(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    async Task<StepLeaseInfo?> IQuestionStepLeases.GetAsync(Guid stepId, CancellationToken ct) =>
        await db.Steps
            .Where(s => s.Id == stepId)
            .Select(s => new StepLeaseInfo(s.Id, s.TaskId, s.LeaseOwner))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task ReleaseAsync(Guid stepId, CancellationToken ct)
    {
        var step = await db.Steps.FirstOrDefaultAsync(s => s.Id == stepId, ct).ConfigureAwait(false);
        if (step is null)
            return;

        db.Entry(step).Property("LeaseOwner").CurrentValue = null;
        db.Entry(step).Property("LeaseExpiresAt").CurrentValue = null;
        if (step.Status == StepStatus.Leased)
            db.Entry(step).Property(s => s.Status).CurrentValue = StepStatus.Pending;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
