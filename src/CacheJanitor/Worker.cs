using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CacheJanitor;

/// <summary>
/// Long-running service: watches every configured cache folder and trims it shortly after anything
/// changes, plus a periodic full check as a safety net. Picks up edits to config.json without a restart.
/// </summary>
public sealed class Worker : BackgroundService
{
    /// <summary>How often the scheduler looks at due scans, retries broken watchers and saves statistics.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    /// <summary>A healthy, quiet folder is still rescanned this often so its age statistics stay current.</summary>
    private static readonly TimeSpan MaxIdle = TimeSpan.FromHours(1);

    /// <summary>How often old churn history is pruned.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(24);

    private readonly IOptionsMonitor<JanitorOptions> _monitor;
    private readonly StatsStore _stats;
    private readonly ChurnStore _churn;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _lock = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private Dictionary<string, WatchedCache> _caches = new(StringComparer.OrdinalIgnoreCase);
    private JanitorOptions _options = new();
    private List<string> _configErrors = new();
    private bool? _lastAccessEnabled;
    private volatile bool _configChanged = true;
    private CancellationToken _stopping;

    public Worker(IOptionsMonitor<JanitorOptions> monitor, StatsStore stats, ChurnStore churn, ILoggerFactory loggers)
    {
        _monitor = monitor;
        _stats = stats;
        _churn = churn;
        _loggers = loggers;
        _log = loggers.CreateLogger<Worker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Return to the host at once: the first scan of a large cache can take longer than the 30 seconds
        // the Service Control Manager allows for start-up.
        await Task.Yield();
        _stopping = stoppingToken;
        _lastAccessEnabled = LastAccessCheck.WarnIfDisabled(_log);

        using var subscription = _monitor.OnChange(_ =>
        {
            _configChanged = true;
            _wake.Release();
        });

        try
        {
            var nextPrune = DateTime.UtcNow;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextPrune)
                {
                    _churn.Prune(DateTime.UtcNow);
                    nextPrune = DateTime.UtcNow + PruneInterval;
                }

                if (_configChanged)
                {
                    // Editors and the config reloader often fire twice for one save; let them settle.
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    _configChanged = false;
                    Apply(_monitor.CurrentValue);
                }

                RunDueScans();
                _stats.SaveIfDirty();

                try { await _wake.WaitAsync(Tick, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "CacheJanitor stopped unexpectedly.");
            Environment.ExitCode = 1; // non-zero so the service's recovery actions restart it
            throw;
        }
        finally
        {
            List<WatchedCache> caches;
            lock (_lock) caches = _caches.Values.ToList();
            await Task.WhenAll(caches.Select(c => c.StopAsync()));
            foreach (var c in caches) c.Dispose();
            _stats.SaveIfDirty();
        }
    }

    public ServiceStatus GetStatus()
    {
        List<WatchedCache> caches;
        JanitorOptions options;
        List<string> errors;
        lock (_lock)
        {
            caches = _caches.Values.ToList();
            options = _options;
            errors = _configErrors.ToList();
        }

        return new ServiceStatus
        {
            Version = typeof(Worker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "",
            StartedUtc = _startedUtc,
            LastAccessEnabled = _lastAccessEnabled,
            ConfigErrors = errors,
            ScanIntervalMinutes = options.ScanIntervalMinutes,
            DebounceSeconds = options.DebounceSeconds,
            Caches = caches.Select(c =>
            {
                var s = c.GetStatus();
                _stats.Fill(c.Path, s);
                return s;
            }).ToList(),
        };
    }

    /// <summary>Starts a pass on one cache, or on all of them when <paramref name="path"/> is empty.</summary>
    public bool RequestTrim(string? path)
    {
        List<WatchedCache> targets;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(path))
                targets = _caches.Values.ToList();
            else if (PathGuard.Check(path) is null && _caches.TryGetValue(PathGuard.Normalize(path), out var one))
                targets = [one];
            else
                return false;
        }
        foreach (var c in targets) _ = c.RunNowAsync();
        return true;
    }

    private void Apply(JanitorOptions options)
    {
        var errors = new List<string>();
        if (options.ScanIntervalMinutes <= 0) errors.Add("ScanIntervalMinutes must be greater than zero; using 5.");
        if (options.DebounceSeconds < 0) errors.Add("DebounceSeconds cannot be negative; using 15.");
        var interval = TimeSpan.FromMinutes(options.ScanIntervalMinutes > 0 ? options.ScanIntervalMinutes : 5);
        var debounce = TimeSpan.FromSeconds(options.DebounceSeconds >= 0 ? options.DebounceSeconds : 15);

        var wanted = new Dictionary<string, CacheFolderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in options.Caches)
        {
            var label = string.IsNullOrWhiteSpace(c.Path) ? "(no path)" : c.Path;
            if (PathGuard.Check(c.Path) is { } problem) { errors.Add($"{label}: {problem}"); continue; }
            if (!SizeParser.TryParse(c.MaxSize, out _)) { errors.Add($"{label}: MaxSize '{c.MaxSize}' is not a size such as 50GB."); continue; }
            var key = PathGuard.Normalize(c.Path);
            if (wanted.Keys.FirstOrDefault(k => PathGuard.IsInside(key, k) || PathGuard.IsInside(k, key) || string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) is { } clash)
            {
                errors.Add($"{label}: overlaps {clash}; ignored.");
                continue;
            }
            c.Path = key;
            wanted[key] = c;
        }

        foreach (var e in errors)
            _log.LogError("Configuration problem: {Problem}", e);

        lock (_lock)
        {
            var next = new Dictionary<string, WatchedCache>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, c) in wanted)
            {
                if (_caches.Remove(key, out var existing) && existing.Matches(c, debounce))
                {
                    existing.Interval = interval;
                    next[key] = existing;
                    continue;
                }
                existing?.Dispose();
                next[key] = new WatchedCache(c, interval, debounce, _stats, _churn, () => _lastAccessEnabled, _loggers.CreateLogger($"CacheJanitor[{key}]"), _stopping);
                _log.LogInformation("Managing {Path}: max {Max}, unit {Unit}, min age {MinAge} min{Disabled}.",
                    key, SizeParser.Format(c.MaxBytes), c.Unit, c.MinAgeMinutes, c.Enabled ? "" : ", eviction OFF");
            }
            foreach (var removed in _caches.Values)
            {
                _log.LogInformation("No longer managing {Path}.", removed.Path);
                removed.Dispose();
            }
            _caches = next;
            _options = options;
            _configErrors = errors;
        }

        _stats.Retain(wanted.Keys);
        if (wanted.Count == 0)
            _log.LogWarning("No caches configured. Add them with the CacheJanitor tray app or in {Config}.", AppPaths.ConfigPath);
    }

    private void RunDueScans()
    {
        List<WatchedCache> caches;
        lock (_lock) caches = _caches.Values.ToList();
        var now = DateTime.UtcNow;
        foreach (var c in caches)
        {
            c.EnsureWatching();
            if (c.IsDue(now, MaxIdle))
                _ = c.RunNowAsync();
        }
    }

    private sealed class WatchedCache : IDisposable
    {
        /// <summary>Sizing advice is recomputed at most this often unless a pass matched returns.</summary>
        private static readonly TimeSpan SizingRefresh = TimeSpan.FromMinutes(15);

        private readonly CacheFolderOptions _options;
        private readonly string _signature;
        private readonly TimeSpan _debounce;
        private readonly StatsStore _stats;
        private readonly ChurnStore _churn;
        private readonly Func<bool?> _lastAccessEnabled;
        private readonly ILogger _log;
        private readonly CancellationTokenSource _cts;
        private readonly Timer _debounceTimer;
        private readonly SemaphoreSlim _run = new(1, 1);
        private readonly ConcurrentQueue<DateTime> _changes = new();
        private readonly CacheTrimmer? _trimmer;
        private FileSystemWatcher? _watcher;
        private int _waiting;
        private Task _current = Task.CompletedTask;
        private volatile bool _changedSinceScan = true;

        private CacheState _state = CacheState.Pending;
        private string? _error;
        private CacheTrimmer.Result? _last;
        private DateTime? _lastScanUtc;
        private DateTime _nextScanUtc = DateTime.UtcNow;
        private SizingAdvice? _sizing;
        private DateTime? _sizingUtc;

        public WatchedCache(CacheFolderOptions options, TimeSpan interval, TimeSpan debounce, StatsStore stats, ChurnStore churn, Func<bool?> lastAccessEnabled, ILogger log, CancellationToken stopping)
        {
            _options = options;
            _signature = Signature(options, debounce);
            _debounce = debounce;
            _stats = stats;
            _churn = churn;
            _lastAccessEnabled = lastAccessEnabled;
            _log = log;
            Interval = interval;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _debounceTimer = new Timer(_ => _ = RunNowAsync(), null, Timeout.Infinite, Timeout.Infinite);
            try
            {
                _trimmer = new CacheTrimmer(options, log);
            }
            catch (FormatException ex)
            {
                Fail(ex.Message);
            }
        }

        public string Path => _options.Path;
        public TimeSpan Interval { get; set; }

        public bool Matches(CacheFolderOptions other, TimeSpan debounce) => _signature == Signature(other, debounce);

        private static string Signature(CacheFolderOptions o, TimeSpan debounce) =>
            ConfigFile.Serialize(new JanitorOptions { Caches = [o] }) + debounce.Ticks;

        public bool IsDue(DateTime now, TimeSpan maxIdle)
        {
            if (now < _nextScanUtc) return false;
            // A watched folder that has not changed cannot have grown, so the periodic scan can wait,
            // unless it is still over its limit with entries that will become old enough to evict.
            bool quiet = _watcher is not null && !_changedSinceScan && _state is CacheState.Ok or CacheState.Disabled;
            if (quiet && _lastScanUtc is { } last && now - last < maxIdle)
            {
                _nextScanUtc = now + Interval;
                return false;
            }
            return true;
        }

        /// <summary>Creates the folder and its watcher if either is missing; retried on every scheduler tick.</summary>
        public void EnsureWatching()
        {
            if (_watcher is not null || _cts.IsCancellationRequested) return;
            try
            {
                Directory.CreateDirectory(_options.Path);
                var w = new FileSystemWatcher(_options.Path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                };
                w.Created += OnChanged;
                w.Changed += OnChanged;
                w.Renamed += OnChanged;
                w.Error += OnError;
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (_error is null)
                    _log.LogWarning("Could not watch {Path}; relying on periodic scans only: {Message}", _options.Path, ex.Message);
                Fail(ex.Message);
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (e.Name is { } name && name.StartsWith(CacheTrimmer.TrashPrefix, StringComparison.OrdinalIgnoreCase))
                return;
            var now = DateTime.UtcNow;
            _changes.Enqueue(now);
            while (_changes.TryPeek(out var t) && now - t > TimeSpan.FromMinutes(1))
                _changes.TryDequeue(out _);
            _changedSinceScan = true;
            Poke();
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            // Buffer overflow just means "lots changed": check now. Anything else (the folder was deleted, a
            // network share dropped): drop the watcher and let the scheduler re-create it.
            _changedSinceScan = true;
            Poke();
            if (e.GetException() is InternalBufferOverflowException) return;
            _log.LogWarning(e.GetException(), "Watcher on {Path} failed; will retry.", _options.Path);
            var w = Interlocked.Exchange(ref _watcher, null);
            if (w is not null)
            {
                w.EnableRaisingEvents = false;
                ThreadPool.QueueUserWorkItem(_ => w.Dispose());
            }
        }

        /// <summary>Something changed: (re)start the debounce timer so we trim once activity settles.</summary>
        private void Poke()
        {
            try { _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Runs a pass now, or after the current one; requests made while one is already waiting are merged into it.</summary>
        public Task RunNowAsync()
        {
            if (_trimmer is null || _cts.IsCancellationRequested) return Task.CompletedTask;
            if (Interlocked.Exchange(ref _waiting, 1) == 1) return Task.CompletedTask;
            var task = RunAsync();
            _current = task;
            return task;
        }

        private async Task RunAsync()
        {
            try
            {
                await _run.WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                Volatile.Write(ref _waiting, 0);
                _changedSinceScan = false;
                var started = DateTime.UtcNow;
                var since = _churn.LastPassUtc(Path);
                var r = await _trimmer!.TrimAsync(_cts.Token, since);
                _last = r;
                _lastScanUtc = DateTime.UtcNow;
                _nextScanUtc = _lastScanUtc.Value + Interval;
                _error = null;
                _state = !_options.Enabled ? CacheState.Disabled
                    : r.BytesAfter > _options.MaxBytes ? CacheState.OverLimit
                    : CacheState.Ok;
                _stats.Record(_options.Path, r, started);
                int matched = _churn.RecordPass(Path, _options.MaxBytes, started, r.Evictions, r.Created);
                if (matched > 0 || _sizingUtc is null || DateTime.UtcNow - _sizingUtc >= SizingRefresh)
                {
                    var now = DateTime.UtcNow;
                    _sizing = SizingAnalyzer.Analyze(new SizingInput(now, _options.MaxBytes, r.BytesAfter,
                        _churn.RecordingSinceUtc(Path), _churn.ReturnsSince(Path, now.AddDays(-30)),
                        r.UntouchedBytes, r.Profile, _lastAccessEnabled()));
                    _sizingUtc = now;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _lastScanUtc = DateTime.UtcNow;
                _nextScanUtc = _lastScanUtc.Value + Interval;
                if (_error != ex.Message)
                    _log.LogError("Trim of {Path} failed: {Message}", _options.Path, ex.Message);
                Fail(ex.Message);
            }
            catch (Exception ex)
            {
                _lastScanUtc = DateTime.UtcNow;
                _nextScanUtc = _lastScanUtc.Value + Interval;
                _log.LogError(ex, "Trim of {Path} failed.", _options.Path);
                Fail(ex.Message);
            }
            finally
            {
                _run.Release();
            }
        }

        private void Fail(string message)
        {
            _state = CacheState.Error;
            _error = message;
        }

        public CacheStatus GetStatus()
        {
            var r = _last;
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(1);
            SizeParser.TryParse(_options.MaxSize, out var max);
            return new CacheStatus
            {
                Path = _options.Path,
                Name = _options.Name,
                State = _state,
                Error = _error,
                MaxBytes = max,
                TargetBytes = max > 0 ? _options.TargetBytes : 0,
                Unit = _options.Unit,
                MinAgeMinutes = _options.MinAgeMinutes,
                CurrentBytes = r?.BytesAfter ?? 0,
                FileCount = r?.Files ?? 0,
                EntryCount = r?.Entries ?? 0,
                LastScanUtc = _lastScanUtc,
                LastScanSeconds = r?.Duration.TotalSeconds ?? 0,
                NextScanUtc = _nextScanUtc,
                OldestUsedUtc = r?.OldestUsedUtc,
                Watching = _watcher is not null,
                ChangesLastMinute = _changes.Count(t => t > cutoff),
                AgeBytes = r?.AgeBytes ?? new long[AgeBuckets.Count],
                AgeEntries = r?.AgeEntries ?? new long[AgeBuckets.Count],
                Sizing = _sizing,
            };
        }

        /// <summary>Cancels any pass in progress and waits for it to finish.</summary>
        public async Task StopAsync()
        {
            _cts.Cancel();
            try { await _current; } catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _watcher?.Dispose();
            _watcher = null;
            _debounceTimer.Dispose();
        }
    }
}
