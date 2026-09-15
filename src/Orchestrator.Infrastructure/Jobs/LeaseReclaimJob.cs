using Orchestrator.Application.Ports;

namespace Orchestrator.Infrastructure.Jobs;

public sealed class LeaseReclaimJob(ILeaseManager leases)
{
    public Task<int> RunAsync(CancellationToken ct) => leases.ReclaimExpiredAsync(ct);
}
