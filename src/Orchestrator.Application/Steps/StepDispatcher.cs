namespace Orchestrator.Application.Steps;

public sealed class StepDispatcher
{
    private readonly Dictionary<string, IStepHandler> _handlers;

    public StepDispatcher(IEnumerable<IStepHandler> handlers)
    {
        _handlers = new Dictionary<string, IStepHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.Capability, handler))
                throw new InvalidOperationException($"Duplicate handler for capability '{handler.Capability}'");
        }
    }

    public async Task<StepOutcome> DispatchAsync(StepContext context, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(context.Capability, out var handler))
            return new StepOutcome(null, null, $"No handler for capability '{context.Capability}'");
        return await handler.HandleAsync(context, ct);
    }
}
