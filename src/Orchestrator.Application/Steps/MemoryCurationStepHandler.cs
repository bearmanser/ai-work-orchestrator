using Orchestrator.Application.Memory;

namespace Orchestrator.Application.Steps;

/// <summary>
/// Runs automatic memory curation when a task completes. Failures surface as
/// step failures; curation never blocks completion itself.
/// </summary>
public sealed class MemoryCurationStepHandler(IMemoryCurator curator) : IStepHandler
{
    public string Capability => "memory.curate";

    public async Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
    {
        await curator.CurateAsync(context.TaskId, ct);
        return new StepOutcome(null, null, null);
    }
}
