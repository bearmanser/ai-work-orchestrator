using System.Globalization;
using System.Text;
using Orchestrator.Application.Ports;

namespace Orchestrator.Adapters.Obsidian;

/// <summary>
/// Serializes and parses the vault's six frontmatter properties, always in order:
/// <c>title</c>, <c>type</c>, <c>area</c>, <c>status</c>, <c>updated</c>, <c>tags</c>.
/// </summary>
internal static class NoteFrontmatterCodec
{
    private static readonly string[] PropertyOrder = ["title", "type", "area", "status", "updated", "tags"];

    /// <summary>Throws <see cref="ArgumentException"/> when any required property is missing.</summary>
    public static void Validate(NoteFrontmatter frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);

        if (string.IsNullOrWhiteSpace(frontmatter.Title))
            throw new ArgumentException("Frontmatter 'title' is required.", nameof(frontmatter));
        if (string.IsNullOrWhiteSpace(frontmatter.Type))
            throw new ArgumentException("Frontmatter 'type' is required.", nameof(frontmatter));
        if (string.IsNullOrWhiteSpace(frontmatter.Area))
            throw new ArgumentException("Frontmatter 'area' is required.", nameof(frontmatter));
        if (string.IsNullOrWhiteSpace(frontmatter.Status))
            throw new ArgumentException("Frontmatter 'status' is required.", nameof(frontmatter));
        if (frontmatter.Tags is null)
            throw new ArgumentException("Frontmatter 'tags' is required.", nameof(frontmatter));
    }

    public static string Serialize(NoteFrontmatter frontmatter, string body)
    {
        Validate(frontmatter);

        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"title: {Quote(frontmatter.Title)}");
        builder.AppendLine($"type: {Quote(frontmatter.Type)}");
        builder.AppendLine($"area: {Quote(frontmatter.Area)}");
        builder.AppendLine($"status: {Quote(frontmatter.Status)}");
        builder.AppendLine($"updated: {frontmatter.Updated.ToString("o", CultureInfo.InvariantCulture)}");
        builder.Append("tags: [");
        builder.Append(string.Join(", ", frontmatter.Tags.Select(Quote)));
        builder.AppendLine("]");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.Append(body);
        return builder.ToString();
    }

    /// <summary>
    /// Splits raw note text into frontmatter and body. Throws
    /// <see cref="InvalidDataException"/> when the frontmatter block or a required
    /// property is missing.
    /// </summary>
    public static (NoteFrontmatter Frontmatter, string Body) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0] != "---")
            throw new InvalidDataException("Note has no frontmatter block.");

        var fence = Array.IndexOf(lines, "---", 1);
        if (fence < 0)
            throw new InvalidDataException("Note has an unterminated frontmatter block.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines[1..fence])
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        foreach (var key in PropertyOrder)
        {
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Note frontmatter is missing required property '{key}'.");
        }

        var body = string.Join('\n', lines[(fence + 1)..]);
        if (body.StartsWith("\n", StringComparison.Ordinal))
            body = body[1..];

        return (new NoteFrontmatter(
            Unquote(values["title"]),
            Unquote(values["type"]),
            Unquote(values["area"]),
            Unquote(values["status"]),
            DateTimeOffset.Parse(values["updated"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ParseTags(values["tags"])), body);
    }

    private static IReadOnlyList<string> ParseTags(string value)
    {
        var inner = value.Trim();
        if (inner.StartsWith('[') && inner.EndsWith(']'))
            inner = inner[1..^1];
        if (string.IsNullOrWhiteSpace(inner))
            return [];
        return inner.Split(',').Select(t => Unquote(t.Trim())).ToArray();
    }

    private static string Quote(string value)
    {
        if (value.Length == 0 || value.Any(c => ":#[{}],&*!|>'\"%@` \t".Contains(c)))
            return '"' + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
        return value;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return trimmed;
    }
}
