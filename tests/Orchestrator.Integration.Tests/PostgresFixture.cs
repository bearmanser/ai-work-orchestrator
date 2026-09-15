using Microsoft.EntityFrameworkCore;
using Orchestrator.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Orchestrator.Integration.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public OrchestratorDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new OrchestratorDbContext(options);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
