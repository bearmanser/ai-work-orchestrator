using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Steps;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Jobs;

public sealed class StepExecutionJob(
    OrchestratorDbContext db,
    StepDispatcher dispatcher,
    ILeaseManager leases,
    NextStepScheduler nextStep,
    TimeProvider clock)
{
    public async Task RunAsync(Guid stepId, CancellationToken ct)
    {
        var step = await db.Steps.SingleOrDefaultAsync(s => s.Id == stepId, ct);
        if (step is null || step.Status is not (StepStatus.Pending or StepStatus.Leased))
            return;

        var owner = $"stepexec-{Guid.NewGuid():N}";
        if (!await leases.TryAcquireAsync(stepId, owner, TimeSpan.FromMinutes(5), ct))
            return;

        await db.Entry(step).ReloadAsync(ct);
        var task = await db.Tasks.SingleOrDefaultAsync(t => t.Id == step.TaskId, ct);
        if (task is null)
        {
            step.Fail("owning task not found", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return;
        }

        var outcome = await dispatcher.DispatchAsync(
            new StepContext(step.TaskId, step.Id, step.Capability, step.InputArtifactId), ct);

        var now = clock.GetUtcNow();
        if (outcome.FailureReason is not null)
            step.Fail(outcome.FailureReason, now);
        else
            step.Succeed(outcome.OutputArtifactId, now);

        if (outcome.Trigger is { } trigger)
        {
            var from = task.State;
            task.Fire(trigger, now);
            // Chains the pipeline: nothing else creates the next Step after this one
            // finishes (see NextStepScheduler's remarks) - without this call, a real
            // task cannot progress past whatever state it just entered.
            await nextStep.ScheduleAsync(task, from, trigger, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task CancelTaskAsync(Guid taskId, CancellationToken ct)
    {
        var open = await db.Steps
            .Where(s => s.TaskId == taskId
                && (s.Status == StepStatus.Pending || s.Status == StepStatus.Leased))
            .ToListAsync(ct);
        foreach (var step in open)
            step.Cancel();
        await db.SaveChangesAsync(ct);
    }
}
