using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Adapters.P1;

/// <summary>A single allowlisted read-only query.</summary>
public sealed record NamedQuery(
    string Name,
    string Sql,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> Environments,
    bool RequiresCompanyParameter);

/// <summary>The allowlisted named-query catalog. There is no API that accepts arbitrary SQL.</summary>
public sealed class NamedQueryCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, NamedQuery> _byName;

    public NamedQueryCatalog(IReadOnlyList<NamedQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        All = queries;
        _byName = new Dictionary<string, NamedQuery>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query.Name))
            {
                throw new ArgumentException("Named query entries must have a non-empty name.", nameof(queries));
            }

            if (string.IsNullOrWhiteSpace(query.Sql))
            {
                throw new ArgumentException("Named query '" + query.Name + "' must have non-empty SQL.", nameof(queries));
            }

            _byName[query.Name] = query;
        }
    }

    public IReadOnlyList<NamedQuery> All { get; }

    public bool TryGet(string name, out NamedQuery? query) => _byName.TryGetValue(name, out query);

    /// <summary>
    /// Loads the catalog from <paramref name="path"/>, or an empty catalog if the
    /// file does not exist. A missing catalog file is a real, currently-shipped
    /// state - <c>src/Orchestrator.Api/config/p1-named-queries.json</c> is not
    /// checked into the repository at all - and until Task 49's acceptance
    /// suite exercised the real planning pipeline end to end, that meant
    /// <see cref="NamedQueryDomainDataSource"/>'s constructor (and therefore
    /// anything that resolves <c>IDomainDataSource</c>, including planning)
    /// threw <see cref="DirectoryNotFoundException"/> on first use in any
    /// environment that has not separately deployed that file. An environment
    /// with no P1 integration configured should simply have zero named
    /// queries, not crash every step that touches the data source.
    /// </summary>
    public static NamedQueryCatalog Load(string path)
    {
        if (!File.Exists(path))
            return new NamedQueryCatalog([]);

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<NamedQueryEntry>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Named query catalog '" + path + "' did not contain a query list.");
        return new NamedQueryCatalog(entries
            .Select(e => new NamedQuery(e.Name, e.Sql, e.Parameters, e.Environments, e.RequiresCompanyParameter))
            .ToList());
    }

    private sealed class NamedQueryEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("sql")]
        public string Sql { get; set; } = string.Empty;

        [JsonPropertyName("parameters")]
        public List<string> Parameters { get; set; } = [];

        [JsonPropertyName("environments")]
        public List<string> Environments { get; set; } = [];

        [JsonPropertyName("requiresCompanyParameter")]
        public bool RequiresCompanyParameter { get; set; }
    }
}
