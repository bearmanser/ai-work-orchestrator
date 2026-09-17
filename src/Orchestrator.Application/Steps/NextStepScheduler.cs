using Orchestrator.Application.Ports;
using Orchestrator.Domain.Steps;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Steps;

/// <summary>
/// Chains the pipeline: after a <see cref="WorkTask"/> fires a trigger, this
/// creates and enqueues whatever <see cref="Step"/> must run next, using
/// <see cref="NextStepFactory"/> for the (from-state, trigger) -&gt; capability
/// mapping.
///
/// This is the piece Task 49 found missing: nothing in production created the
/// next Step after one finished, so no real task could progress past plan
/// confirmation. <see cref="StepExecutionJob"/>, <see cref="Plans.PlanConfirmationService"/>
/// and <see cref="PullRequests.PullRequestFeedbackRouter"/> all call this immediately
/// after firing a trigger on the task, before saving it.
///
/// <see cref="TaskState.Ready"/> is transient: nothing dispatches
/// <see cref="TaskTrigger.WorkDispatched"/> anywhere else, so a task that just
/// became Ready would sit idle forever even though a coding_agent.execute step was
/// created for it. This scheduler fires WorkDispatched itself (Ready -&gt; Running)
/// as part of creating that step, so the caller's single subsequent SaveTaskAsync
/// persists both the original trigger's transition and this one together.
/// </summary>
public sealed class NextStepScheduler(IStepStore steps, IOutboxWriter outbox, TimeProvider clock)
{
    /// <summary>
    /// Call after <paramref name="task"/>.Fire(<paramref name="firedTrigger"/>, ...)
    /// has already run, and before the caller persists the task. Returns the step
    /// created, or null when the (from, trigger) pair needs no next step.
    /// </summary>
    public async Task<Step?> ScheduleAsync(
        WorkTask task, TaskState from, TaskTrigger firedTrigger, CancellationToken ct)
    {
        var capability = NextStepFactory.CapabilityFor(from, firedTrigger);
        if (capability is null)
            return null;

        var now = clock.GetUtcNow();
        if (task.State == TaskState.Ready)
            task.Fire(TaskTrigger.WorkDispatched, now);

        var step = new Step(task.Id, capability, null, null, now);
        outbox.Enqueue("step.dispatch", new { stepId = step.Id }, task.Id, $"step:{step.Id}");
        // IStepStore.AddAsync saves immediately; the outbox message staged above on
        // the same DbContext commits with it, so the step and its dispatch message
        // never exist one without the other.
        await steps.AddAsync(step, ct);
        return step;
    }
}
