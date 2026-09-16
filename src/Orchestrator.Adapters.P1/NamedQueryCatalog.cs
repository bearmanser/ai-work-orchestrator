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

    public static NamedQueryCatalog Load(string path)
    {
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
