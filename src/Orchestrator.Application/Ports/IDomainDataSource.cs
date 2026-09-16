namespace Orchestrator.Application.Ports;

/// <summary>A request to execute one allowlisted named query in a given environment.</summary>
/// <param name="QueryName">The catalog entry name. Arbitrary SQL is never accepted.</param>
/// <param name="Environment">The target environment (e.g. staging). Must be listed on the entry.</param>
/// <param name="Company">The company the query is scoped to. Required when the entry says so.</param>
/// <param name="Parameters">Named parameter values, bound through <c>DbParameter</c>.</param>
public sealed record DomainQueryRequest(
    string QueryName,
    string Environment,
    string Company,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>The rows returned by a named query.</summary>
public sealed record DomainCheckResult(
    string QueryName,
    string Environment,
    string Company,
    bool Passed,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string Description);

/// <summary>Thrown when the requested name is not in the allowlisted catalog.</summary>
public sealed class UnknownNamedQueryException : Exception
{
    public UnknownNamedQueryException(string queryName)
        : base("Unknown named query '" + queryName + "'.")
    {
        QueryName = queryName;
    }

    public string QueryName { get; }
}

/// <summary>Thrown before any connection is attempted when the environment is not listed on the entry.</summary>
public sealed class EnvironmentNotPermittedException : Exception
{
    public EnvironmentNotPermittedException(string environment, string queryName)
        : base("Environment '" + environment + "' is not permitted for named query '" + queryName + "'.")
    {
        Environment = environment;
        QueryName = queryName;
    }

    public string Environment { get; }

    public string QueryName { get; }
}

/// <summary>Executes allowlisted, read-only named queries against a domain data source.</summary>
public interface IDomainDataSource
{
    Task<DomainCheckResult> ExecuteNamedQueryAsync(DomainQueryRequest request, CancellationToken ct);
}
