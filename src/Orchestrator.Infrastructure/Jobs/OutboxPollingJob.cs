using Orchestrator.Infrastructure.Outbox;

namespace Orchestrator.Infrastructure.Jobs;

public sealed class OutboxPollingJob(OutboxDispatcher dispatcher)
{
    public async Task RunAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            if (await dispatcher.DispatchBatchAsync(50, ct) == 0)
                break;
        }
    }
}
