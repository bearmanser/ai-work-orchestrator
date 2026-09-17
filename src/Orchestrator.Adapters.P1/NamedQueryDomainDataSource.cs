using Npgsql;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;

namespace Orchestrator.Adapters.P1;

/// <summary>
/// Read-only domain data source restricted to an allowlisted named-query catalog.
/// Connection strings are per environment, resolved from <see cref="ISecretStore"/>
/// by <c>p1.{environment}.connection</c>; the catalog carries no production entry,
/// so production requests are rejected before any connection is attempted.
/// Values are bound through <see cref="NpgsqlParameter"/>; SQL text is never
/// composed from input. Every query runs in a read-only transaction with a
/// 30-second command timeout. Secret values are never logged.
/// </summary>
public sealed class NamedQueryDomainDataSource : IDomainDataSource
{
    private const int CommandTimeoutSeconds = 30;

    private readonly NamedQueryCatalog _catalog;
    private readonly ISecretStore _secrets;

    public NamedQueryDomainDataSource(P1Options options, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        _catalog = NamedQueryCatalog.Load(options.CatalogPath);
        _secrets = secrets;
    }

    public async Task<DomainCheckResult> ExecuteNamedQueryAsync(DomainQueryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_catalog.TryGet(request.QueryName, out var query) || query is null)
        {
            throw new UnknownNamedQueryException(request.QueryName);
        }

        if (!query.Environments.Contains(request.Environment, StringComparer.OrdinalIgnoreCase))
        {
            throw new EnvironmentNotPermittedException(request.Environment, query.Name);
        }

        if (query.RequiresCompanyParameter && string.IsNullOrWhiteSpace(request.Company))
        {
            throw new ArgumentException("Company is required by named query '" + query.Name + "'.", nameof(request));
        }

        var secretKey = "p1." + request.Environment.ToLowerInvariant() + ".connection";
        var connectionString = await _secrets.GetAsync(new SecretRef(secretKey), ct).ConfigureAwait(false);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(query.Sql, connection, transaction);
        command.CommandTimeout = CommandTimeoutSeconds;
        foreach (var name in query.Parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, ResolveParameter(name, query, request)));
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row[reader.GetName(i)] = value is DBNull ? null : value;
                }

                rows.Add(row);
            }
        }

        return new DomainCheckResult(
            query.Name,
            request.Environment,
            request.Company,
            rows.Count > 0,
            rows,
            query.Name + " returned " + rows.Count + " row(s) in " + request.Environment + ".");
    }

    private static string ResolveParameter(string name, NamedQuery query, DomainQueryRequest request)
    {
        if (name.Equals("company", StringComparison.OrdinalIgnoreCase))
        {
            return request.Company;
        }

        if (request.Parameters.TryGetValue(name, out var value) && value is not null)
        {
            return value;
        }

        throw new ArgumentException(
            "Missing required parameter '" + name + "' for named query '" + query.Name + "'.",
            nameof(request));
    }
}
