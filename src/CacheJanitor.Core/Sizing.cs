namespace CacheJanitor;

/// <summary>The service's overall judgement of a cache's size limit.</summary>
public enum SizingVerdict
{
    /// <summary>Too little history has been recorded to judge; see <see cref="SizingAdvice.RecordingSinceUtc"/>.</summary>
    NotEnoughHistory,
    AboutRight,
    /// <summary>Entries are evicted and fetched again within days often enough that a larger limit would pay off.</summary>
    TooSmall,
    /// <summary>A large share of the cache has gone unused for 90+ days. Less certain than <see cref="TooSmall"/>.</summary>
    CouldBeSmaller,
}

/// <summary>Sizing advice for one cache, estimated from its eviction and return history and its current contents.</summary>
public sealed class SizingAdvice
{
    public SizingVerdict Verdict { get; set; }

    /// <summary>The recommended size limit for <see cref="SizingVerdict.TooSmall"/> and <see cref="SizingVerdict.CouldBeSmaller"/>; otherwise null.</summary>
    public long? SuggestedMaxBytes { get; set; }

    /// <summary>When eviction history for this cache began.</summary>
    public DateTime RecordingSinceUtc { get; set; }

    /// <summary>Re-downloaded bytes of entries that returned within 7 days of eviction, over the last 30 days.</summary>
    public long ThrashBytes { get; set; }

    /// <summary>Number of returns counted in <see cref="ThrashBytes"/>.</summary>
    public int ThrashEntries { get; set; }

    /// <summary>The part of <see cref="ThrashBytes"/> from returns under 1 MB each.</summary>
    public long SmallThrashBytes { get; set; }

    /// <summary>Number of returns counted in <see cref="SmallThrashBytes"/>.</summary>
    public int SmallThrashEntries { get; set; }

    /// <summary>Median time between eviction and return of the thrash in <see cref="ThrashBytes"/>, in hours; null when there was none.</summary>
    public double? MedianGoneHours { get; set; }

    /// <summary>Re-downloaded bytes of entries that returned more than 7 days after eviction, over the last 30 days.</summary>
    public long BreakBytes { get; set; }

    /// <summary>Bytes currently in the cache whose last use was 90+ days ago.</summary>
    public long UntouchedBytes { get; set; }

    /// <summary>True when most thrash came from entries recorded as unused for 30+ days before eviction, so this folder's last-access times look unreliable.</summary>
    public bool LastAccessUnreliable { get; set; }

    /// <summary>True when <see cref="SizingVerdict.CouldBeSmaller"/> would apply but is not offered because NTFS last-access updates are off.</summary>
    public bool ShrinkWithheld { get; set; }

    /// <summary>Estimated weekly thrash at limits from about half to about twice the current one, in ascending order of limit.</summary>
    public List<SizingPoint> Curve { get; set; } = new();

    /// <summary>The entries with the most re-downloaded bytes over the last 30 days, largest first, at most ten.</summary>
    public List<ReturnedEntry> TopReturned { get; set; } = new();
}

/// <summary>One point of the what-if curve: estimated weekly thrash at a given size limit.</summary>
public sealed class SizingPoint
{
    public long MaxBytes { get; set; }
    public long WeeklyThrashBytes { get; set; }

    /// <summary>False when the estimate goes beyond the recorded evidence; such points are drawn dashed.</summary>
    public bool Confident { get; set; }
}

/// <summary>An entry that was evicted and fetched again in the last 30 days.</summary>
public sealed class ReturnedEntry
{
    public string Entry { get; set; } = "";

    /// <summary>Size of the entry at its most recent return.</summary>
    public long Bytes { get; set; }

    /// <summary>Number of times it returned.</summary>
    public int Returns { get; set; }

    /// <summary>Re-downloaded bytes over all its returns.</summary>
    public long TotalBytes { get; set; }
}
