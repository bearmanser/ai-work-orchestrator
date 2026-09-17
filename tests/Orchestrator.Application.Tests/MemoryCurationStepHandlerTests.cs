using Orchestrator.Application.Steps;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class MemoryCurationStepHandlerTests
{
    [Fact]
    public async Task Handle_RunsCurationForTheStepTask()
    {
        var curator = new RecordingCurator();
        var handler = new MemoryCurationStepHandler(curator);
        var taskId = Guid.CreateVersion7();

        var outcome = await handler.HandleAsync(
            new StepContext(taskId, Guid.CreateVersion7(), "memory.curate", null), default);

        outcome.FailureReason.ShouldBeNull();
        outcome.Trigger.ShouldBeNull();
        curator.CuratedTasks.ShouldBe([taskId]);
    }

    private sealed class RecordingCurator : Application.Memory.IMemoryCurator
    {
        public List<Guid> CuratedTasks { get; } = [];

        public Task<Application.Memory.CurationResult> CurateAsync(Guid taskId, CancellationToken ct)
        {
            CuratedTasks.Add(taskId);
            return Task.FromResult(new Application.Memory.CurationResult([], []));
        }

        public Task RevertAsync(Guid proposalId, CancellationToken ct) => Task.CompletedTask;
    }
}
