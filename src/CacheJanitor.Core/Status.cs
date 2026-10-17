namespace CacheJanitor;

/// <summary>Everything the service reports about itself, as returned by the "status" pipe request.</summary>
public sealed class ServiceStatus
{
    public string Version { get; set; } = "";
    public DateTime StartedUtc { get; set; }

    /// <summary>False when NTFS last-access updates are off, so eviction is by write time rather than true LRU.</summary>
    public bool? LastAccessEnabled { get; set; }

    /// <summary>Problems with config.json. Cache entries named here are not being managed.</summary>
    public List<string> ConfigErrors { get; set; } = new();

    public double ScanIntervalMinutes { get; set; }
    public double DebounceSeconds { get; set; }
    public List<CacheStatus> Caches { get; set; } = new();
}

public enum CacheState
{
    /// <summary>Not yet scanned since the service started.</summary>
    Pending,
    Ok,
    /// <summary>Over its limit, but everything left is too new, in use or excluded.</summary>
    OverLimit,
    /// <summary>The folder does not exist or the last pass failed; see <see cref="CacheStatus.Error"/>.</summary>
    Error,
    /// <summary>Measured only; eviction is switched off for this folder.</summary>
    Disabled,
}

public sealed class CacheStatus
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public CacheState State { get; set; }
    public string? Error { get; set; }

    public long MaxBytes { get; set; }
    public long TargetBytes { get; set; }
    public EvictionUnit Unit { get; set; }
    public double MinAgeMinutes { get; set; }

    public long CurrentBytes { get; set; }
    public long FileCount { get; set; }

    /// <summary>Number of evictable units: files, or top-level entries in <see cref="EvictionUnit.TopLevelEntry"/> mode.</summary>
    public long EntryCount { get; set; }

    public DateTime? LastScanUtc { get; set; }
    public double LastScanSeconds { get; set; }
    public DateTime? NextScanUtc { get; set; }

    /// <summary>Last-used time of the oldest entry: how far back the cache reaches.</summary>
    public DateTime? OldestUsedUtc { get; set; }

    /// <summary>Whether the folder watcher is running; without it changes are only seen on periodic scans.</summary>
    public bool Watching { get; set; }

    /// <summary>File system change notifications in the last minute.</summary>
    public int ChangesLastMinute { get; set; }

    /// <summary>Bytes and entry counts by last-used age, one per <see cref="AgeBuckets.Labels"/> bucket.</summary>
    public long[] AgeBytes { get; set; } = new long[AgeBuckets.Count];
    public long[] AgeEntries { get; set; } = new long[AgeBuckets.Count];

    public CacheTotals Totals { get; set; } = new();
    public List<HistorySample> History { get; set; } = new();
    public List<EvictionRecord> RecentEvictions { get; set; } = new();

    /// <summary>Sizing advice from the recorded churn history; null until it has been computed.</summary>
    public SizingAdvice? Sizing { get; set; }
}

/// <summary>Counters that accumulate across passes and service restarts.</summary>
public sealed class CacheTotals
{
    public DateTime SinceUtc { get; set; }
    public long Passes { get; set; }
    public long EvictedBytes { get; set; }
    public long EvictedEntries { get; set; }

    /// <summary>Bytes that appeared between passes: new downloads, estimated from size growth plus evictions.</summary>
    public long IngressBytes { get; set; }

    /// <summary>Evictions that failed because a tool had the entry open.</summary>
    public long InUseSkips { get; set; }

    /// <summary>How long ago the most recently evicted entry had last been used. Short means the cache is too small.</summary>
    public double? LastEvictedAgeHours { get; set; }
}

/// <summary>One slot of size history. Byte counters cover the slot; sizes are as of its last pass.</summary>
public sealed class HistorySample
{
    public DateTime Utc { get; set; }
    public long Bytes { get; set; }
    public long Files { get; set; }
    public long IngressBytes { get; set; }
    public long EvictedBytes { get; set; }
    public int Evictions { get; set; }
}

public sealed class EvictionRecord
{
    public DateTime Utc { get; set; }
    public string Entry { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime LastUsedUtc { get; set; }
}

public static class AgeBuckets
{
    public static readonly string[] Labels = ["< 1 hour", "1–24 hours", "1–7 days", "7–30 days", "> 30 days"];
    public static int Count => Labels.Length;

    public static int Of(TimeSpan age) => age.TotalHours switch
    {
        < 1 => 0,
        < 24 => 1,
        < 24 * 7 => 2,
        < 24 * 30 => 3,
        _ => 4,
    };
}
