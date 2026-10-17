using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CacheJanitor;

/// <summary>
/// Long-running service: watches every configured cache folder and trims it shortly after anything
/// changes, plus a periodic full check as a safety net.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly JanitorOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly List<WatchedCache> _caches = new();

    public Worker(IOptions<JanitorOptions> options, ILoggerFactory loggers)
    {
        _options = options.Value;
        _loggers = loggers;
        _log = loggers.CreateLogger<Worker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Caches.Count == 0)
        {
            _log.LogError("No caches configured. Add entries under \"Janitor:Caches\" in appsettings.json.");
            return;
        }

        LastAccessCheck.WarnIfDisabled(_log);

        foreach (var c in _options.Caches)
        {
            Directory.CreateDirectory(c.Path);
            var trimmer = new CacheTrimmer(c, _loggers.CreateLogger($"CacheJanitor[{c.Path}]"));
            var watched = new WatchedCache(trimmer, TimeSpan.FromSeconds(_options.DebounceSeconds), _log, stoppingToken);
            _caches.Add(watched);
            _log.LogInformation("Managing {Path}: max {Max}, unit {Unit}, min age {MinAge} min.",
                c.Path, SizeParser.Format(c.MaxBytes), c.Unit, c.MinAgeMinutes);
        }

        // Initial pass, then periodic passes.
        var interval = TimeSpan.FromMinutes(Math.Max(0.1, _options.ScanIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                await Task.WhenAll(_caches.Select(c => c.RunNowAsync()));
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        foreach (var c in _caches) c.Dispose();
        base.Dispose();
    }

    private sealed class WatchedCache : IDisposable
    {
        private readonly CacheTrimmer _trimmer;
        private readonly TimeSpan _debounce;
        private readonly ILogger _log;
        private readonly CancellationToken _ct;
        private readonly Timer _debounceTimer;
        private FileSystemWatcher? _watcher;

        public WatchedCache(CacheTrimmer trimmer, TimeSpan debounce, ILogger log, CancellationToken ct)
        {
            _trimmer = trimmer;
            _debounce = debounce;
            _log = log;
            _ct = ct;
            _debounceTimer = new Timer(_ => _ = RunNowAsync(), null, Timeout.Infinite, Timeout.Infinite);
            StartWatcher();
        }

        private void StartWatcher()
        {
            try
            {
                _watcher?.Dispose();
                _watcher = new FileSystemWatcher(_trimmer.Root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                };
                _watcher.Created += (_, _) => Poke();
                _watcher.Changed += (_, _) => Poke();
                _watcher.Renamed += (_, _) => Poke();
                _watcher.Error += (_, e) =>
                {
                    // Buffer overflow just means "lots changed" -> check now. Other errors: re-create the watcher.
                    _log.LogDebug(e.GetException(), "Watcher error on {Path}", _trimmer.Root);
                    Poke();
                    if (e.GetException() is not InternalBufferOverflowException)
                        StartWatcher();
                };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not watch {Path}; relying on periodic scans only.", _trimmer.Root);
            }
        }

        /// <summary>Something changed: (re)start the debounce timer so we trim once activity settles.</summary>
        private void Poke() => _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);

        public async Task RunNowAsync()
        {
            if (_ct.IsCancellationRequested) return;
            try
            {
                await _trimmer.TrimAsync(_ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Trim of {Path} failed.", _trimmer.Root);
            }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _debounceTimer.Dispose();
        }
    }
}
