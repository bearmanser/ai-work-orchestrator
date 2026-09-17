using System.Text.Json;
using Orchestrator.Application.Gateway;
using Orchestrator.Application.Operations;
using Orchestrator.Application.Policy;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Operations;
using Shouldly;

namespace Orchestrator.Application.Tests;

public class ToolGatewayTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();
    private static readonly Guid RepoA = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static AuthorizationEnvelope Envelope() => new(
        TaskId,
        "requester@kameleongruppen.no",
        [new RepositoryScope(RepoA, ["main"], ["*"], RepositoryAccess.Write)],
        ["staging"],
        ["KAMELEON-TEST"],
        [],
        [],
        [],
        [],
        [],
        [],
        new BudgetLimits(100m, TimeSpan.FromHours(1), 10, 10),
        Now.AddHours(1),
        Now.AddMinutes(-1));

    private static BudgetLedger Ledger() => new(TaskId, new BudgetLimits(100m, TimeSpan.FromHours(1), 10, 10));

    private static ToolCall Call() => new(
        TaskId,
        Guid.CreateVersion7(),
        "requester@kameleongruppen.no",
        ActionKind.RepositoryRead,
        ResourceScope.Repository(RepoA, "main"),
        $"op-{Guid.CreateVersion7()}",
        $"key-{Guid.CreateVersion7()}",
        "test call",
        Envelope(),
        Ledger());

    private static ToolGateway Gateway(InMemoryToolCallStore? store = null, List<string>? reorder = null)
    {
        var effectiveStore = store ?? new InMemoryToolCallStore(reorder);
        return new ToolGateway(
            new PolicyEngine(new RecordingAuditWriter(), PolicyRules.Default, new NoOpOperationalControls()),
            effectiveStore,
            new RecordingAuditWriter(),
            new ManualTimeProvider(Now));
    }

    [Fact]
    public async Task Invoke_DeniedByPolicy_DoesNotCallTheAdapter()
    {
        var called = false;
        var gateway = Gateway();
        var denied = Call() with
        {
            Action = ActionKind.DomainRead,
            Resource = ResourceScope.P1("production", "KAMELEON-LIVE"),
        };

        var result = await gateway.InvokeAsync(denied, _ => { called = true; return Task.FromResult(1); }, default);

        called.ShouldBeFalse();
        result.Executed.ShouldBeFalse();
        result.Decision.ShouldBeOfType<PolicyDecision.Denied>();
    }

    [Fact]
    public async Task Invoke_ApprovalRequired_DoesNotCallTheAdapter()
    {
        var called = false;
        var approval = Call() with
        {
            Action = ActionKind.DomainWrite,
            Resource = ResourceScope.P1("staging", "KAMELEON-TEST"),
        };
        var result = await Gateway()
            .InvokeAsync(approval, _ => { called = true; return Task.FromResult(1); }, default);

        called.ShouldBeFalse();
        result.Decision.ShouldBeOfType<PolicyDecision.ApprovalRequired>();
    }

    [Fact]
    public async Task Invoke_Permitted_RecordsIntentBeforeInvoking()
    {
        var order = new List<string>();
        var gateway = Gateway(reorder: order);

        await gateway.InvokeAsync(Call(), _ => { order.Add("invoke"); return Task.FromResult(1); }, default);

        order.ShouldBe(["record-intent", "invoke", "record-outcome"]);
    }

    [Fact]
    public async Task Invoke_AdapterThrows_LeavesOutcomeUnknown_NotFailed()
    {
        // An exception mid-call cannot prove the remote side effect did not happen.
        var store = new InMemoryToolCallStore();
        var gateway = Gateway(store: store);

        await Should.ThrowAsync<HttpRequestException>(() =>
            gateway.InvokeAsync<int>(Call(), _ => throw new HttpRequestException("timeout"), default));

        store.Records.Single().Outcome.ShouldBe(ToolCallOutcome.Unknown);
    }

    [Fact]
    public async Task Invoke_SameIdempotencyKeyTwice_SecondReturnsTheRecordedResult()
    {
        var store = new InMemoryToolCallStore();
        var gateway = Gateway(store: store);
        var call = Call() with { IdempotencyKey = "pr:repo-a:task-1" };

        await gateway.InvokeAsync(call, _ => Task.FromResult(42), default);
        var invocations = 0;
        var second = await gateway.InvokeAsync(call, _ => { invocations++; return Task.FromResult(99); }, default);

        invocations.ShouldBe(0);
        second.Value.ShouldBe(42);
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task RecordAsync(AuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryToolCallStore(List<string>? order = null) : IToolCallStore
    {
        public List<ToolCallRecord> Records { get; } = [];

        public Task<ToolCallRecord?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
        {
            return Task.FromResult(Records.FirstOrDefault(r => r.IdempotencyKey == idempotencyKey));
        }

        public Task<ToolCallRecord> RecordIntentAsync(ToolCall call, PolicyDecision decision, DateTimeOffset now, CancellationToken ct)
        {
            order?.Add("record-intent");
            var record = new ToolCallRecord(
                Guid.CreateVersion7(),
                call.TaskId,
                call.StepId,
                call.OperationId,
                call.IdempotencyKey,
                ToolCallOutcome.Unknown,
                decision,
                null,
                now,
                null);
            Records.Add(record);
            return Task.FromResult(record);
        }

        public Task RecordSuccessAsync<TResult>(Guid recordId, TResult? value, DateTimeOffset now, CancellationToken ct)
        {
            order?.Add("record-outcome");
            var existing = Records.Single(r => r.Id == recordId);
            var json = value is null ? null : JsonSerializer.Serialize(value);
            existing.Complete(ToolCallOutcome.Succeeded, json, now);
            return Task.CompletedTask;
        }

        public TResult? Deserialize<TResult>(ToolCallRecord record)
        {
            return record.ResultJson is null ? default : JsonSerializer.Deserialize<TResult>(record.ResultJson);
        }

        public Task<bool> HasUnknownOutcomesAsync(Guid taskId, CancellationToken ct)
        {
            return Task.FromResult(Records.Any(r => r.TaskId == taskId && r.Outcome == ToolCallOutcome.Unknown));
        }

        public Task<int> ReconcileUnknownAsync(Guid taskId, DateTimeOffset now, CancellationToken ct)
        {
            var unknown = Records.Where(r => r.TaskId == taskId && r.Outcome == ToolCallOutcome.Unknown).ToList();
            foreach (var record in unknown)
                record.Complete(ToolCallOutcome.Succeeded, record.ResultJson, now);
            return Task.FromResult(unknown.Count);
        }
    }

    /// <summary>
    /// A fixed clock so envelope expiry is judged against the same <see cref="Now"/>
    /// the test fixtures were built with, not the real wall clock - using
    /// TimeProvider.System here made every test in this file flake the day after it
    /// was written, once real time passed the envelope's fixed ExpiresAt.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoOpOperationalControls : IOperationalControls
    {
        public Task PauseIntakeAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task ResumeIntakeAsync(string actor, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> IsIntakePausedAsync(CancellationToken ct) => Task.FromResult(false);
        public Task DisableIntegrationAsync(string name, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task EnableIntegrationAsync(string name, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> IsIntegrationDisabledAsync(string name, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> CancelTaskAsync(Guid taskId, string actor, CancellationToken ct) => Task.FromResult(false);
        public Task RevokeAuthorizationAsync(Guid taskId, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task RetryStepAsync(Guid taskId, Guid stepId, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task ReconcileTaskAsync(Guid taskId, string actor, CancellationToken ct) => Task.CompletedTask;
    }
}
