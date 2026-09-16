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

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("xoxb-1234567890-abcdefghijklmnop")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    public async Task Record_DetailsContainingKnownSecretShape_IsRejected(string secretShapedValue)
    {
        var writer = new AuditWriter(fixture.NewDbContext(), TimeProvider.System);

        await Should.ThrowAsync<ArgumentException>(() => writer.RecordAsync(new AuditEntry(
            "admin", null, "integration.configure", "set token", "Permitted", $"corr-{Guid.NewGuid()}",
            new Dictionary<string, string> { ["token"] = secretShapedValue }), default));
    }

    [Theory]
    [InlineData("my-org/my-repo")]
    [InlineData("a1b2c3d4e5f678901234567890abcdef12345678")] // 40-char hex commit sha
    [InlineData("feature/orders")]
    [InlineData("environment 'production' not in authorization envelope")]
    [InlineData("b174a444-146f-46ec-9d69-d5863bce9ebf")] // UUID
    [InlineData("Denied")] // outcome word
    [InlineData("QmFzZTY0LWlzaC1hcnRpZmFjdC1oYXNoLXZhbHVl")] // base64-ish artifact hash
    public async Task Record_DetailsContainingOrdinaryValue_IsAccepted(string ordinaryValue)
    {
        var taskId = Guid.CreateVersion7();
        var writer = new AuditWriter(fixture.NewDbContext(), TimeProvider.System);

        await writer.RecordAsync(new AuditEntry(
            "admin", taskId, "integration.configure", "set token", "Permitted", $"corr-{Guid.NewGuid()}",
            new Dictionary<string, string> { ["value"] = ordinaryValue }), default);

        await using var db = fixture.NewDbContext();
        (await db.AuditEvents.CountAsync(e => e.TaskId == taskId)).ShouldBe(1);
    }

    [Fact]
    public async Task Record_ReasonContainingSecretShapedValue_IsStoredRedacted()
    {
        var taskId = Guid.CreateVersion7();
        var writer = new AuditWriter(fixture.NewDbContext(), TimeProvider.System);

        await writer.RecordAsync(new AuditEntry(
            "orchestrator", taskId, "adapter.call",
            Reason: "GET https://api.example.com/repo?token=ghp_abcdefghijklmnopqrstuvwxyz0123456789 failed with 401",
            Outcome: "Failed",
            CorrelationId: $"corr-{Guid.NewGuid()}",
            Details: null), default);

        await using var db = fixture.NewDbContext();
        var entry = await db.AuditEvents.SingleAsync(e => e.TaskId == taskId);
        entry.Reason.ShouldBe("GET https://api.example.com/repo?token=[redacted] failed with 401");
        entry.Reason.ShouldNotContain("ghp_");
    }

    [Fact]
    public async Task Record_ReasonWithoutSecretShapedValue_IsStoredUnchanged()
    {
        var taskId = Guid.CreateVersion7();
        var writer = new AuditWriter(fixture.NewDbContext(), TimeProvider.System);
        const string reason = "environment 'production' not in authorization envelope";

        await writer.RecordAsync(new AuditEntry(
            "orchestrator", taskId, "policy.evaluate", reason, "Denied", $"corr-{Guid.NewGuid()}", null), default);

        await using var db = fixture.NewDbContext();
        var entry = await db.AuditEvents.SingleAsync(e => e.TaskId == taskId);
        entry.Reason.ShouldBe(reason);
    }
}
