using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Adapters.P1;
using Orchestrator.Application.Ports;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Secrets;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class NamedQueryDomainDataSourceTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string ReaderRole = "p1_reader";
    private const string ReaderPassword = "p1ReaderPw0372";

    private readonly List<string> _tempDirs = [];

    public async Task InitializeAsync() => await SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Execute_KnownQueryInAllowedEnvironment_ReturnsRows()
    {
        var source = CreateSource();

        var result = await source.ExecuteNamedQueryAsync(
            new DomainQueryRequest("picked-order-count", "staging", "KAMELEON-TEST",
                new Dictionary<string, string> { ["fromDate"] = "2026-09-01" }),
            default);

        result.Passed.ShouldBeTrue();
        result.Rows.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Execute_UnknownQueryName_Throws() =>
        await Should.ThrowAsync<UnknownNamedQueryException>(() => CreateSource().ExecuteNamedQueryAsync(
            new DomainQueryRequest("whatever-i-want", "staging", "KAMELEON-TEST", new Dictionary<string, string>()),
            default));

    [Fact]
    public async Task Execute_ProductionEnvironment_Throws()
    {
        var ex = await Should.ThrowAsync<EnvironmentNotPermittedException>(() => CreateSource().ExecuteNamedQueryAsync(
            new DomainQueryRequest("picked-order-count", "production", "KAMELEON-LIVE",
                new Dictionary<string, string>()),
            default));

        ex.Message.ShouldContain("production");
    }

    [Fact]
    public async Task Execute_MissingRequiredCompany_Throws() =>
        await Should.ThrowAsync<ArgumentException>(() => CreateSource().ExecuteNamedQueryAsync(
            new DomainQueryRequest("picked-order-count", "staging", "", new Dictionary<string, string>()),
            default));

    [Fact]
    public async Task Execute_ParameterContainingSqlSyntax_IsBoundNotInterpolated()
    {
        var source = CreateSource();

        var result = await source.ExecuteNamedQueryAsync(
            new DomainQueryRequest("order-by-reference", "staging", "KAMELEON-TEST",
                new Dictionary<string, string> { ["reference"] = "'; DROP TABLE orders; --" }),
            default);

        result.Rows.ShouldBeEmpty();
        (await OrdersTableExistsAsync()).ShouldBeTrue();
    }

    [Fact]
    public void Catalog_ContainsNoDataModifyingStatements()
    {
        var forbidden = new[] { "insert ", "update ", "delete ", "drop ", "alter ", "truncate ", "exec ", "merge " };
        foreach (var query in NamedQueryCatalog.Load(CatalogPath()).All)
        {
            var sql = query.Sql.ToLowerInvariant();
            forbidden.ShouldNotContain(f => sql.Contains(f, StringComparison.Ordinal),
                "named query '" + query.Name + "' is not read-only");
        }
    }

    [Fact]
    public async Task Execute_UsesAReadOnlyConnection()
    {
        await Should.ThrowAsync<DbException>(() => ExecuteRawAsReaderAsync("CREATE TABLE should_not_exist (id int)"));
    }

    private NamedQueryDomainDataSource CreateSource()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p1-adapter-tests-" + Guid.NewGuid())).FullName;
        _tempDirs.Add(dir);
        File.WriteAllText(Path.Combine(dir, "p1.staging.connection"), RestrictedConnectionString());
        return new NamedQueryDomainDataSource(new P1Options(CatalogPath()), new FileSecretStore(dir));
    }

    private async Task SeedAsync()
    {
        await using var db = fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(File.ReadAllText(SchemaPath()));
    }

    private async Task<bool> OrdersTableExistsAsync()
    {
        await using var db = fixture.NewDbContext();
        return await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM information_schema.tables WHERE table_schema = 'p1' AND table_name = 'orders'")
            .SingleAsync() == 1;
    }

    private async Task ExecuteRawAsReaderAsync(string sql)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(RestrictedConnectionString())
            .Options;
        await using var db = new OrchestratorDbContext(options);
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private string RestrictedConnectionString()
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = fixture.ConnectionString };
        builder["Username"] = ReaderRole;
        builder["Password"] = ReaderPassword;
        return builder.ConnectionString;
    }

    private static string CatalogPath() => Path.Combine(RepoRoot(), "config", "p1-named-queries.json");

    private static string SchemaPath() => Path.Combine(RepoRoot(), "tests", "fixtures", "p1-schema.sql");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orchestrator.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Orchestrator.sln not found above the test output directory");
        return dir.FullName;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
