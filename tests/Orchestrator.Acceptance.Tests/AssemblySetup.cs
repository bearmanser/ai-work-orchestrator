using Orchestrator.Integration.Tests;

// The acceptance suite shares one Postgres container (see AcceptanceCollection
// below - starting one per test class turned out to be more than this
// sandbox's Docker daemon reliably handles) and Environment-variable-based
// secrets (see AcceptanceHost.SetSecretEnvironmentVariables) across every
// AcceptanceHost instance. None of that is safe under xUnit's default
// same-assembly test parallelism, so it is disabled here - each test class
// still gets its own AcceptanceHost (its own git root, WireMock servers and
// API host), but test classes run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Orchestrator.Acceptance.Tests;

[CollectionDefinition(nameof(AcceptanceCollection))]
public sealed class AcceptanceCollection : ICollectionFixture<PostgresFixture>;
