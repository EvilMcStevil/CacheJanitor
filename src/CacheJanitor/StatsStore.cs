using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CacheJanitor;

/// <summary>
/// Lifetime counters, size history and recent evictions for every cache, persisted to
/// <see cref="AppPaths.StatsPath"/> so the tray's charts survive service restarts.
/// </summary>
public sealed class StatsStore
{
    /// <summary>Width of one history slot. Passes inside the same slot are merged.</summary>
    public static readonly TimeSpan SlotWidth = TimeSpan.FromMinutes(5);

    /// <summary>How much history is kept per cache.</summary>
    public static readonly TimeSpan HistoryLength = TimeSpan.FromDays(7);

    private const int MaxRecentEvictions = 100;

    private readonly ILogger _log;
    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, CacheRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public sealed class CacheRecord
    {
        public CacheTotals Totals { get; set; } = new();
        public List<HistorySample> History { get; set; } = new();
        public List<EvictionRecord> RecentEvictions { get; set; } = new();

        /// <summary>Size left after the previous pass; growth beyond it is counted as ingress.</summary>
        public long? BytesAfterLastPass { get; set; }
    }

    public StatsStore(ILogger<StatsStore> log) : this(log, AppPaths.StatsPath) { }

    public StatsStore(ILogger log, string path)
    {
        _log = log;
        _path = path;
        Load();
    }

    public void Record(string cachePath, CacheTrimmer.Result r, DateTime utc)
    {
        lock (_lock)
        {
            var rec = Get(cachePath);
            long ingress = rec.BytesAfterLastPass is { } prev ? Math.Max(0, r.BytesBefore - prev) : 0;
            long evictedBytes = r.BytesBefore - r.BytesAfter;
            rec.BytesAfterLastPass = r.BytesAfter;

            var t = rec.Totals;
            t.Passes++;
            t.IngressBytes += ingress;
            t.EvictedBytes += evictedBytes;
            t.EvictedEntries += r.Evicted;
            t.InUseSkips += r.SkippedInUse;
            if (r.Evictions.Count > 0)
                t.LastEvictedAgeHours = (utc - r.Evictions[^1].LastUsedUtc).TotalHours;

            var slot = new DateTime(utc.Ticks - utc.Ticks % SlotWidth.Ticks, DateTimeKind.Utc);
            var last = rec.History.Count > 0 ? rec.History[^1] : null;
            if (last is null || last.Utc != slot)
            {
                last = new HistorySample { Utc = slot };
                rec.History.Add(last);
            }
            last.Bytes = r.BytesAfter;
            last.Files = r.Files;
            last.IngressBytes += ingress;
            last.EvictedBytes += evictedBytes;
            last.Evictions += r.Evicted;

            var oldest = utc - HistoryLength;
            int drop = rec.History.FindIndex(h => h.Utc >= oldest);
            if (drop > 0) rec.History.RemoveRange(0, drop);

            rec.RecentEvictions.AddRange(r.Evictions);
            if (rec.RecentEvictions.Count > MaxRecentEvictions)
                rec.RecentEvictions.RemoveRange(0, rec.RecentEvictions.Count - MaxRecentEvictions);

            _dirty = true;
        }
    }

    /// <summary>Copies one cache's record into <paramref name="status"/>.</summary>
    public void Fill(string cachePath, CacheStatus status)
    {
        lock (_lock)
        {
            var rec = Get(cachePath);
            status.Totals = Clone(rec.Totals);
            status.History = rec.History.Select(Clone).ToList();
            status.RecentEvictions = rec.RecentEvictions.AsEnumerable().Reverse().Select(Clone).ToList();
        }
    }

    /// <summary>Drops records for caches that are no longer configured.</summary>
    public void Retain(IEnumerable<string> cachePaths)
    {
        lock (_lock)
        {
            var keep = new HashSet<string>(cachePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var key in _records.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _records.Remove(key);
                _dirty = true;
            }
        }
    }

    public void SaveIfDirty()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_records, PipeProtocol.Json);
            _dirty = false;
        }
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save statistics to {Path}: {Message}", _path, ex.Message);
        }
    }

    private CacheRecord Get(string cachePath)
    {
        var key = PathGuard.Normalize(cachePath);
        if (!_records.TryGetValue(key, out var rec))
        {
            rec = new CacheRecord { Totals = { SinceUtc = DateTime.UtcNow } };
            _records[key] = rec;
        }
        return rec;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheRecord>>(File.ReadAllText(_path), PipeProtocol.Json);
            if (loaded is not null)
                _records = new Dictionary<string, CacheRecord>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.LogWarning("Could not read statistics from {Path}, starting fresh: {Message}", _path, ex.Message);
        }
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, PipeProtocol.Json), PipeProtocol.Json)!;
}
