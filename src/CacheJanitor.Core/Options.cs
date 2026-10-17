using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CacheJanitor;

public enum EvictionUnit
{
    /// <summary>Each file is evicted on its own (good for flat download caches).</summary>
    File,
    /// <summary>Each top-level file or folder is evicted as a whole (good when tools unpack into a folder per item).</summary>
    TopLevelEntry,
}

public sealed class JanitorOptions
{
    /// <summary>How often to do a full scan even if nothing changed.</summary>
    public double ScanIntervalMinutes { get; set; } = 5;

    /// <summary>After a change is seen in a folder, wait this long for activity to settle before checking.</summary>
    public double DebounceSeconds { get; set; } = 15;

    public List<CacheFolderOptions> Caches { get; set; } = new();
}

public sealed class CacheFolderOptions
{
    public string Path { get; set; } = "";

    /// <summary>Optional display name; the path is shown when empty.</summary>
    public string Name { get; set; } = "";

    /// <summary>When false the folder is still measured but nothing is evicted from it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Max size, e.g. "50GB", "750MB", "1.5TB" or a plain byte count. Units are binary (1GB = 1024^3).</summary>
    public string MaxSize { get; set; } = "";

    /// <summary>When over MaxSize, trim down to this percentage of it so we don't evict on every new file.</summary>
    public double TrimToPercent { get; set; } = 90;

    public EvictionUnit Unit { get; set; } = EvictionUnit.File;

    /// <summary>Never evict anything written or accessed in the last N minutes (protects in-flight downloads/builds).</summary>
    public double MinAgeMinutes { get; set; } = 10;

    /// <summary>Glob patterns (relative to Path, * and ? and **) that are never evicted, e.g. "**/*.lock".</summary>
    public List<string> Exclude { get; set; } = new();

    [JsonIgnore]
    public long MaxBytes => SizeParser.Parse(MaxSize);

    [JsonIgnore]
    public long TargetBytes => (long)(MaxBytes * Math.Clamp(TrimToPercent, 1, 100) / 100.0);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Path : Name;
}

public static class SizeParser
{
    private static readonly Regex Pattern = new(@"^\s*([\d.]+)\s*([KMGT]?)(I?B)?\s*$", RegexOptions.IgnoreCase);

    public static bool TryParse(string? text, out long bytes)
    {
        try
        {
            bytes = Parse(text ?? "");
            return bytes > 0;
        }
        catch (FormatException)
        {
            bytes = 0;
            return false;
        }
    }

    public static long Parse(string text)
    {
        var m = Pattern.Match(text ?? "");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new FormatException($"Can't parse size '{text}'. Use e.g. 50GB, 750MB, 1.5TB or a byte count.");

        var multiplier = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1L << 10,
            "M" => 1L << 20,
            "G" => 1L << 30,
            "T" => 1L << 40,
            _ => 1L,
        };
        return (long)(number * multiplier);
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        while (Math.Abs(v) >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return string.Create(CultureInfo.InvariantCulture, $"{v:0.##} {units[i]}");
    }
}
