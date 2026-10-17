using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CacheJanitor.Tray.Charts;

namespace CacheJanitor.Tray;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Refreshes every binding on this object.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        Edited?.Invoke();
        return true;
    }

    /// <summary>Raised when a user-editable property changes.</summary>
    public event Action? Edited;
}

/// <summary>One cache folder: its editable settings, plus the service's latest status for it.</summary>
public sealed class CacheItem : Observable
{
    private string _path, _name, _maxSize, _excludeText;
    private double _trimToPercent, _minAgeMinutes;
    private EvictionUnit _unit;
    private bool _enabled;
    private CacheStatus? _status;

    public CacheItem(CacheFolderOptions o)
    {
        _path = o.Path;
        _name = o.Name;
        _maxSize = o.MaxSize;
        _trimToPercent = o.TrimToPercent;
        _minAgeMinutes = o.MinAgeMinutes;
        _unit = o.Unit;
        _enabled = o.Enabled;
        _excludeText = string.Join(Environment.NewLine, o.Exclude);
    }

    public string Path { get => _path; set { if (Set(ref _path, value)) RaiseAll(); } }
    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(Title)); } }
    public string MaxSize { get => _maxSize; set { if (Set(ref _maxSize, value)) RaiseAll(); } }
    public double TrimToPercent { get => _trimToPercent; set { if (Set(ref _trimToPercent, Math.Round(value))) RaiseAll(); } }
    public double MinAgeMinutes { get => _minAgeMinutes; set => Set(ref _minAgeMinutes, value); }
    public EvictionUnit Unit { get => _unit; set => Set(ref _unit, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string ExcludeText { get => _excludeText; set => Set(ref _excludeText, value); }

    public CacheFolderOptions ToOptions() => new()
    {
        Path = Path.Trim(),
        Name = Name.Trim(),
        MaxSize = MaxSize.Trim(),
        TrimToPercent = TrimToPercent,
        MinAgeMinutes = MinAgeMinutes,
        Unit = Unit,
        Enabled = Enabled,
        Exclude = ExcludeText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
    };

    /// <summary>Set by the window on each poll; null when the service does not know this folder (yet).</summary>
    public CacheStatus? Status
    {
        get => _status;
        set { _status = value; RaiseAll(); }
    }

    /// <summary>Why there is no status: set alongside a null <see cref="Status"/>.</summary>
    public string? NoStatusReason { get; set; }

    // ---- list and header ----

    public string Title => !string.IsNullOrWhiteSpace(Name) ? Name : string.IsNullOrWhiteSpace(Path) ? "New folder" : System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : Path;
    public string Subtitle => string.IsNullOrWhiteSpace(Path) ? "Choose a folder in Settings" : Path;

    public long LimitBytes => SizeParser.TryParse(MaxSize, out var b) ? b : 0;
    public long TargetBytes => (long)(LimitBytes * Math.Clamp(TrimToPercent, 1, 100) / 100);
    public long UsedBytes => Status?.CurrentBytes ?? 0;
    public double Fraction => LimitBytes > 0 && Status is { LastScanUtc: not null } ? UsedBytes / (double)LimitBytes : 0;
    public double TargetFraction => Math.Clamp(TrimToPercent, 1, 100) / 100;
    public Level Level => Status is null ? Level.Neutral : ChartTheme.LevelOf(Fraction, Status.State == CacheState.Error);

    public string UsageText => Status is { LastScanUtc: not null } s
        ? $"{Fmt.Size(s.CurrentBytes)} of {Fmt.Size(LimitBytes)}"
        : LimitBytes > 0 ? $"Limit {Fmt.Size(LimitBytes)}" : "No limit set";

    public string PercentText => Status is { LastScanUtc: not null } ? Fmt.Percent(Fraction) : "";

    public string StateText => Status switch
    {
        null => NoStatusReason ?? "No data",
        { State: CacheState.Pending } => "Waiting for first scan",
        { State: CacheState.Ok } => Fraction >= 0.9 ? "Near limit" : "OK",
        { State: CacheState.OverLimit } => "Over limit: nothing old enough to evict",
        { State: CacheState.Error } s => s.Error ?? "Error",
        { State: CacheState.Disabled } => "Measuring only (eviction off)",
        _ => "",
    };

    public SizingAdvice? Sizing => Status?.Sizing;

    public string AdviceText => Sizing?.Verdict switch
    {
        SizingVerdict.TooSmall => "↑ Too small",
        SizingVerdict.CouldBeSmaller => "↓ Could be smaller",
        SizingVerdict.AboutRight => "✓ About right",
        _ => "",
    };

    public Level AdviceLevel => Sizing?.Verdict == SizingVerdict.TooSmall ? Level.Warning : Level.Neutral;

    /// <summary>The state line, or the sizing advice in its place when the state is plain OK or Near limit.</summary>
    public bool ShowsAdvice => AdviceText.Length > 0 && StateText is "OK" or "Near limit";

    public string StateGlyph => Status?.State switch
    {
        CacheState.Ok when Fraction < 0.9 => "",
        CacheState.Ok or CacheState.OverLimit => "",
        CacheState.Error => "",
        CacheState.Disabled => "",
        _ => "",
    };

    // ---- overview tiles ----

    public string FilesText => Status is { } s ? Fmt.Count(s.FileCount) : "—";
    public string FilesSub => Status is { } s && s.Unit == EvictionUnit.TopLevelEntry ? $"in {Fmt.Count(s.EntryCount)} top-level entries" : "files in the cache";

    public string ReachText => Status?.OldestUsedUtc is { } t ? Fmt.Duration(DateTime.UtcNow - t) : "—";
    public string ReachSub => Status?.OldestUsedUtc is { } t ? $"oldest item last used {t.ToLocalTime():d MMM}" : "how far back the cache goes";

    public string LastEvictedText => Status?.Totals.LastEvictedAgeHours is { } h ? Fmt.Duration(TimeSpan.FromHours(h)) : "—";
    public string LastEvictedSub => Status?.Totals.LastEvictedAgeHours is { } h
        ? h < 24 ? "idle before eviction: cache may be too small" : "idle before eviction"
        : "nothing evicted yet";
    public Level LastEvictedLevel => Status?.Totals.LastEvictedAgeHours is { } h && h < 24 ? Level.Warning : Level.Neutral;

    public string EvictedText => Status is { } s ? Fmt.Size(s.Totals.EvictedBytes) : "—";
    public string EvictedSub => Status is { } s ? $"{Fmt.Count(s.Totals.EvictedEntries)} entries since {s.Totals.SinceUtc.ToLocalTime():d MMM}" : "";

    public string AddedText => Status is { } s ? Fmt.Size(s.Totals.IngressBytes) : "—";
    public string AddedSub => Status is { } s ? $"estimated since {s.Totals.SinceUtc.ToLocalTime():d MMM}" : "";

    public string Added24hText => Status is { } s ? Fmt.Size(s.History.Where(h => h.Utc > DateTime.UtcNow.AddDays(-1)).Sum(h => h.IngressBytes)) : "—";
    public string Evicted24hText => Status is { } s ? Fmt.Size(s.History.Where(h => h.Utc > DateTime.UtcNow.AddDays(-1)).Sum(h => h.EvictedBytes)) : "—";

    /// <summary>Bytes added per day over the last week, as a share of the limit: how many times a day the cache turns over.</summary>
    public string TurnoverText
    {
        get
        {
            if (Status is not { } s || LimitBytes == 0 || s.History.Count < 2) return "—";
            var span = DateTime.UtcNow - s.History[0].Utc;
            if (span < TimeSpan.FromHours(1)) return "—";
            double perDay = s.History.Sum(h => h.IngressBytes) / span.TotalDays;
            return Fmt.Size((long)perDay) + " / day";
        }
    }

    // ---- activity ----

    public string LastScanText => Status?.LastScanUtc is { } t ? $"{Fmt.Ago(t)} (took {(Status.LastScanSeconds < 1 ? $"{Status.LastScanSeconds * 1000:0} ms" : $"{Status.LastScanSeconds.ToString("0.#", CultureInfo.CurrentCulture)} s")})" : "not yet";
    public string NextScanText => Fmt.Until(Status?.NextScanUtc);
    public string WatchingText => Status is null ? "—" : Status.Watching ? $"Yes · {Fmt.Count(Status.ChangesLastMinute)} changes in the last minute" : "No: periodic scans only";
    public string PassesText => Status is { } s ? Fmt.Count(s.Totals.Passes) : "—";
    public string InUseText => Status is { } s ? Fmt.Count(s.Totals.InUseSkips) : "—";
    public string PolicyText => Status is { } s
        ? $"{(s.Unit == EvictionUnit.File ? "Individual files" : "Whole top-level entries")}, nothing used in the last {Fmt.Duration(TimeSpan.FromMinutes(s.MinAgeMinutes))}"
        : "—";

    public IEnumerable<EvictionRow> RecentEvictions => (Status?.RecentEvictions ?? []).Select(e => new EvictionRow(e));
}

public sealed class EvictionRow(EvictionRecord e)
{
    public string When => Fmt.LocalTime(e.Utc);
    public string Entry => e.Entry;
    public string Size => Fmt.Size(e.Bytes);
    public string IdleFor => Fmt.Duration(e.Utc - e.LastUsedUtc);
}

/// <summary>The dashboard: the settings being edited and the service's state.</summary>
public sealed class MainModel : Observable
{
    private CacheItem? _selected;
    private double _scanIntervalMinutes, _debounceSeconds;
    private bool _isDirty;
    private string _savedJson = "";

    public ObservableCollection<CacheItem> Caches { get; } = new();

    public CacheItem? Selected
    {
        get => _selected;
        set { _selected = value; Raise(); Raise(nameof(HasSelection)); }
    }

    public bool HasSelection => _selected is not null;

    public double ScanIntervalMinutes { get => _scanIntervalMinutes; set { if (Set(ref _scanIntervalMinutes, value)) UpdateDirty(); } }
    public double DebounceSeconds { get => _debounceSeconds; set { if (Set(ref _debounceSeconds, value)) UpdateDirty(); } }

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (_isDirty != value) { _isDirty = value; Raise(); } }
    }

    public void Load(JanitorOptions options)
    {
        var selectedPath = Selected?.Path;
        foreach (var c in Caches) c.Edited -= UpdateDirty;
        Caches.Clear();
        foreach (var o in options.Caches)
            Add(new CacheItem(o));
        _scanIntervalMinutes = options.ScanIntervalMinutes;
        _debounceSeconds = options.DebounceSeconds;
        Raise(nameof(ScanIntervalMinutes));
        Raise(nameof(DebounceSeconds));
        _savedJson = ConfigFile.Serialize(ToOptions());
        IsDirty = false;
        Selected = Caches.FirstOrDefault(c => string.Equals(c.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? Caches.FirstOrDefault();
    }

    public void Add(CacheItem item)
    {
        item.Edited += UpdateDirty;
        Caches.Add(item);
        UpdateDirty();
    }

    public void Remove(CacheItem item)
    {
        item.Edited -= UpdateDirty;
        int i = Caches.IndexOf(item);
        Caches.Remove(item);
        Selected = Caches.Count == 0 ? null : Caches[Math.Min(i, Caches.Count - 1)];
        UpdateDirty();
    }

    public JanitorOptions ToOptions() => new()
    {
        ScanIntervalMinutes = ScanIntervalMinutes,
        DebounceSeconds = DebounceSeconds,
        Caches = Caches.Select(c => c.ToOptions()).ToList(),
    };

    public void MarkSaved()
    {
        _savedJson = ConfigFile.Serialize(ToOptions());
        IsDirty = false;
    }

    private void UpdateDirty() => IsDirty = ConfigFile.Serialize(ToOptions()) != _savedJson;
}
