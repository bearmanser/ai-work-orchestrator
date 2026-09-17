using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain.PullRequests;
using Orchestrator.Infrastructure.Persistence;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class PullRequestTrackingStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Upsert_RoundTrips_WithFeedbackJson()
    {
        var taskId = Guid.CreateVersion7();
        var tracking = new PullRequestTracking(
            taskId, Guid.CreateVersion7(), 4711, "https://example.com/pr/4711",
            "abc", true, DateTimeOffset.UtcNow);
        tracking.RecordFeedback(new FeedbackItem(7, "U-reviewer", "please rename", "abc", FeedbackHandling.Unhandled));

        await using (var db = fixture.NewDbContext())
        {
            await new EfPullRequestTrackingStore(db).UpsertAsync(tracking, default);
        }

        await using var read = fixture.NewDbContext();
        var loaded = await read.PullRequestTracking.SingleAsync(t => t.TaskId == taskId);

        loaded.PullRequestId.ShouldBe(4711);
        loaded.Feedback.Single().Text.ShouldBe("please rename");
    }

    [Fact]
    public async Task Upsert_SameTaskAndRepositoryTwice_KeepsASingleRow()
    {
        var taskId = Guid.CreateVersion7();
        var repositoryId = Guid.CreateVersion7();

        await using (var seed = fixture.NewDbContext())
        {
            var store = new EfPullRequestTrackingStore(seed);
            await store.UpsertAsync(new PullRequestTracking(
                taskId, repositoryId, 4711, "https://example.com/pr/4711",
                "abc", true, DateTimeOffset.UtcNow), default);
        }

        await using (var retry = fixture.NewDbContext())
        {
            var store = new EfPullRequestTrackingStore(retry);
            await store.UpsertAsync(new PullRequestTracking(
                taskId, repositoryId, 4711, "https://example.com/pr/4711",
                "abc", true, DateTimeOffset.UtcNow), default);
        }

        await using var read = fixture.NewDbContext();
        var tracked = await read.PullRequestTracking
            .Where(t => t.TaskId == taskId)
            .ToListAsync();

        tracked.Count.ShouldBe(1);
        tracked.Single().PullRequestId.ShouldBe(4711);
    }
}
