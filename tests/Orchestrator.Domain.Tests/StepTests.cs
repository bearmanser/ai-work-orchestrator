using Orchestrator.Domain.Steps;
using Shouldly;

namespace Orchestrator.Domain.Tests;

public class StepTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static Step NewStep() => new(Guid.CreateVersion7(), "implementation", null, null, Now);

    [Fact]
    public void RetryReset_FromFailed_Succeeds()
    {
        var step = NewStep();
        step.Fail("boom", Now);

        step.RetryReset(Now.AddMinutes(1));

        step.Status.ShouldBe(StepStatus.Pending);
        step.FailureReason.ShouldBeNull();
    }

    [Fact]
    public void RetryReset_FromPending_IsRefused()
    {
        var step = NewStep();

        Should.Throw<InvalidOperationException>(() => step.RetryReset(Now.AddMinutes(1)));
    }

    [Fact]
    public void RetryReset_FromALiveLease_IsRefused()
    {
        // The worker holding this lease may still be executing; resetting to
        // Pending here would let a second worker run the same side effect twice.
        var step = NewStep();
        step.Lease("worker-1", Now.AddMinutes(10));

        Should.Throw<InvalidOperationException>(() => step.RetryReset(Now.AddMinutes(1)));

        step.Status.ShouldBe(StepStatus.Leased);
    }

    [Fact]
    public void RetryReset_FromAnExpiredLease_IsAllowed()
    {
        // Nothing is running any more; reclaiming an abandoned lease is exactly
        // what a retry should do.
        var step = NewStep();
        step.Lease("worker-1", Now.AddMinutes(10));

        step.RetryReset(Now.AddMinutes(11));

        step.Status.ShouldBe(StepStatus.Pending);
        step.LeaseOwner.ShouldBeNull();
        step.LeaseExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void RetryReset_OnSuccess_IncrementsAttemptCount()
    {
        var step = NewStep();
        step.Fail("boom", Now); // AttemptCount -> 1

        step.RetryReset(Now.AddMinutes(1));

        step.AttemptCount.ShouldBe(2);
    }

    [Fact]
    public void RetryReset_FromLeaseAtExactExpiry_IsAllowed()
    {
        // now == LeaseExpiresAt: the lease is no longer "not yet expired", so this
        // is the reclaim case, not the live-worker case.
        var step = NewStep();
        step.Lease("worker-1", Now.AddMinutes(10));

        step.RetryReset(Now.AddMinutes(10));

        step.Status.ShouldBe(StepStatus.Pending);
    }
}
