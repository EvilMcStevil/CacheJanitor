using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CacheJanitor;

/// <summary>
/// Reads and writes <see cref="AppPaths.ConfigPath"/>. The file holds a single "Janitor" section in the same
/// shape the service binds through Microsoft.Extensions.Configuration.
/// </summary>
public static class ConfigFile
{
    public const string SectionName = "Janitor";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static JanitorOptions Load(string path)
    {
        if (!File.Exists(path))
            return new JanitorOptions();
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var section = root?[SectionName];
        return section?.Deserialize<JanitorOptions>(Json) ?? new JanitorOptions();
    }

    public static string Serialize(JanitorOptions options) =>
        new JsonObject { [SectionName] = JsonSerializer.SerializeToNode(options, Json) }.ToJsonString(Json);

    /// <summary>Writes the file atomically, so the service never reloads a half-written config.</summary>
    public static void Save(string path, JanitorOptions options)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, Serialize(options));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Problems that would stop a cache entry from being managed, one message per problem.</summary>
    public static List<string> Validate(JanitorOptions options)
    {
        var errors = new List<string>();
        if (options.ScanIntervalMinutes <= 0) errors.Add("Scan interval must be greater than zero.");
        if (options.DebounceSeconds < 0) errors.Add("Debounce cannot be negative.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in options.Caches)
        {
            var label = string.IsNullOrWhiteSpace(c.Path) ? "(no path)" : c.DisplayName;
            if (PathGuard.Check(c.Path) is { } problem)
                errors.Add($"{label}: {problem}");
            else if (!seen.Add(PathGuard.Normalize(c.Path)))
                errors.Add($"{label}: listed more than once.");
            if (!SizeParser.TryParse(c.MaxSize, out _))
                errors.Add($"{label}: max size '{c.MaxSize}' is not a size such as 50GB or 750MB.");
            if (c.TrimToPercent is < 1 or > 100)
                errors.Add($"{label}: trim-to must be between 1 and 100%.");
            if (c.MinAgeMinutes < 0)
                errors.Add($"{label}: minimum age cannot be negative.");
        }

        foreach (var a in options.Caches)
        foreach (var b in options.Caches)
        {
            if (ReferenceEquals(a, b) || PathGuard.Check(a.Path) is not null || PathGuard.Check(b.Path) is not null) continue;
            if (PathGuard.IsInside(PathGuard.Normalize(b.Path), PathGuard.Normalize(a.Path)))
                errors.Add($"{b.DisplayName}: is inside {a.DisplayName}; nested caches would evict each other's files.");
        }
        return errors;
    }
}
