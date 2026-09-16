using Microsoft.Data.Sqlite;

namespace Orchestrator.Adapters.Obsidian;

/// <summary>
/// Rebuildable full-text index over vault notes. The SQLite file lives under the
/// configured index directory (orchestrator data), never inside the vault itself,
/// so deleting it only costs a rescan.
/// </summary>
public sealed class VaultSearchIndex
{
    private readonly string _connectionString;

    public VaultSearchIndex(string indexDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexDirectory);
        Directory.CreateDirectory(indexDirectory);
        var dbPath = Path.Combine(Path.GetFullPath(indexDirectory), "vault-index.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public async Task UpsertAsync(string path, string content, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO notes (path, content) VALUES (@path, @content) "
            + "ON CONFLICT (path) DO UPDATE SET content = excluded.content";
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@content", content);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Returns indexed paths whose content matches the text, ordered by path.</summary>
    public async Task<IReadOnlyList<string>> SearchAsync(string text, int maxResults, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);

        await using var command = connection.CreateCommand();
        if (string.IsNullOrEmpty(text))
        {
            command.CommandText = "SELECT path FROM notes ORDER BY path LIMIT @limit";
        }
        else
        {
            command.CommandText = "SELECT path FROM notes WHERE content LIKE @pattern ESCAPE '\\' "
                + "ORDER BY path LIMIT @limit";
            command.Parameters.AddWithValue("@pattern", "%" + EscapeLike(text) + "%");
        }

        command.Parameters.AddWithValue("@limit", maxResults);

        var matches = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            matches.Add(reader.GetString(0));
        return matches;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS notes "
            + "(path TEXT PRIMARY KEY, content TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
