using Hangfire;
using Orchestrator.Application.Ports;

namespace Orchestrator.Infrastructure.Jobs;

public sealed class HangfireJobScheduler(IBackgroundJobClient jobs) : IJobScheduler
{
    public Task EnqueueStepAsync(Guid stepId, CancellationToken ct)
    {
        jobs.Enqueue<StepExecutionJob>(job => job.RunAsync(stepId, CancellationToken.None));
        return Task.CompletedTask;
    }

    public Task RequestCancellationAsync(Guid taskId, CancellationToken ct)
    {
        jobs.Enqueue<StepExecutionJob>(job => job.CancelTaskAsync(taskId, CancellationToken.None));
        return Task.CompletedTask;
    }
}
