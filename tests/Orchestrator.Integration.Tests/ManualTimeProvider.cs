namespace Orchestrator.Integration.Tests;

/// <summary>
/// A minimal, manually-advanced <see cref="TimeProvider"/> for tests that need to
/// control exactly when time passes (e.g. proving a backoff window is honored, or a
/// lease has expired) without real delays or sleeps. This repository does not
/// otherwise reference the Microsoft.Extensions.TimeProvider.Testing package's
/// FakeTimeProvider, so this small shared type avoids adding a new package
/// dependency. Originally added alongside the outbox tests (Task 4); shared here so
/// later test classes (e.g. lease and step-execution tests) do not duplicate it.
/// Public (rather than internal) so the acceptance suite (Task 49), which needs the
/// exact same controllable clock across a genuine host restart, references this
/// project instead of adding a second fake-clock type.
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
