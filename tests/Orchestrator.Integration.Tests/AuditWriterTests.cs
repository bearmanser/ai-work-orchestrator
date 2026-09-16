using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Audit;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public class AuditWriterTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Record_PolicyDenial_IsQueryableByTaskAndAction()
    {
        var taskId = Guid.CreateVersion7();
        var writer = new AuditWriter(fixture.NewDbContext(), TimeProvider.System);

        await writer.RecordAsync(new AuditEntry(
            Actor: "orchestrator",
            TaskId: taskId,
            Action: "policy.evaluate",
            Reason: "environment 'production' not in authorization envelope",
            Outcome: "Denied",
            CorrelationId: "corr-1",
            Details: new Dictionary<string, string> { ["environment"] = "production" }), default);

        await using var db = fixture.NewDbContext();
        var entry = await db.AuditEvents.SingleAsync(e => e.TaskId == taskId);
        entry.Outcome.ShouldBe("Denied");
        entry.Action.ShouldBe("policy.evaluate");
    }

    [Fact]
    public async Task Record_DetailsContainingSecretShapedValue_IsRejected()
    {
        var writer = new AuditWriter(fixture.NewDbContext(), TimeProvider.System);

        await Should.ThrowAsync<ArgumentException>(() => writer.RecordAsync(new AuditEntry(
            "admin", null, "integration.configure", "set token", "Permitted", "corr-2",
            new Dictionary<string, string> { ["token"] = "ghp_abcdefghijklmnopqrstuvwxyz0123456789" }), default));
    }
}
