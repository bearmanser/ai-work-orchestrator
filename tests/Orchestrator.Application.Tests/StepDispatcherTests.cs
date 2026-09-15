using Orchestrator.Application.Steps;
using Orchestrator.Domain.Tasks;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class StepDispatcherTests
{
    [Fact]
    public async Task Dispatch_UnknownCapability_ReturnsFailureWithoutThrowing()
    {
        var dispatcher = new StepDispatcher([]);
        var outcome = await dispatcher.DispatchAsync(
            new StepContext(Guid.CreateVersion7(), Guid.CreateVersion7(), "nope", null), default);

        outcome.FailureReason.ShouldNotBeNull();
        outcome.FailureReason.ShouldContain("No handler");
        outcome.Trigger.ShouldBeNull();
    }

    [Fact]
    public async Task Dispatch_KnownCapability_RoutesToHandler()
    {
        var handler = new StubHandler("planning", new StepOutcome(TaskTrigger.PlanPosted, "artifact-1", null));
        var dispatcher = new StepDispatcher([handler]);

        var outcome = await dispatcher.DispatchAsync(
            new StepContext(Guid.CreateVersion7(), Guid.CreateVersion7(), "planning", null), default);

        outcome.Trigger.ShouldBe(TaskTrigger.PlanPosted);
        handler.Invocations.ShouldBe(1);
    }

    [Fact]
    public void Construct_TwoHandlersForSameCapability_Throws()
    {
        Should.Throw<InvalidOperationException>(() => new StepDispatcher(
            [new StubHandler("planning", new StepOutcome(null, null, null)),
             new StubHandler("planning", new StepOutcome(null, null, null))]));
    }

    private sealed class StubHandler(string capability, StepOutcome outcome) : IStepHandler
    {
        public string Capability => capability;
        public int Invocations { get; private set; }
        public Task<StepOutcome> HandleAsync(StepContext context, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(outcome);
        }
    }
}
