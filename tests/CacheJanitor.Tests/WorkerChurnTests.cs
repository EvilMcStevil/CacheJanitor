using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CacheJanitor.Tests;

[TestClass]
public class WorkerChurnTests
{
    private string _dir = "";

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"worker-churn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "cache"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FixedOptions(JanitorOptions value) : IOptionsMonitor<JanitorOptions>
    {
        public JanitorOptions CurrentValue => value;
        public JanitorOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<JanitorOptions, string?> listener) => null;
    }

    private string MakeFile(string cache, string name, int kb, TimeSpan age)
    {
        var p = Path.Combine(cache, name);
        File.WriteAllBytes(p, new byte[kb * 1024]);
        var t = DateTime.UtcNow - age;
        File.SetCreationTimeUtc(p, t);
        File.SetLastWriteTimeUtc(p, t);
        File.SetLastAccessTimeUtc(p, t);
        return p;
    }

    private static async Task<DateTime?> WaitForPass(Worker worker, DateTime? after)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var scan = worker.GetStatus().Caches.FirstOrDefault()?.LastScanUtc;
            if (scan is not null && (after is null || scan > after)) return scan;
            await Task.Delay(50);
        }
        throw new TimeoutException("No pass completed within 10 seconds.");
    }

    [TestMethod]
    public async Task EvictedFileThatReappears_IsRecordedAsAReturn()
    {
        var cache = Path.Combine(_dir, "cache");
        var options = new JanitorOptions
        {
            Caches = [new CacheFolderOptions { Path = cache, MaxSize = "100KB", TrimToPercent = 50, MinAgeMinutes = 0 }],
        };
        MakeFile(cache, "a.bin", 40, TimeSpan.FromDays(3));
        MakeFile(cache, "b.bin", 40, TimeSpan.FromDays(2));
        MakeFile(cache, "c.bin", 40, TimeSpan.FromDays(1));

        var stats = new StatsStore(NullLogger.Instance, Path.Combine(_dir, "stats.json"));
        using var churn = new ChurnStore(NullLogger.Instance, Path.Combine(_dir, "churn.db"));
        var worker = new Worker(new FixedOptions(options), stats, churn, NullLoggerFactory.Instance);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            var first = await WaitForPass(worker, null);
            File.Exists(Path.Combine(cache, "a.bin")).ShouldBeFalse();

            MakeFile(cache, "a.bin", 40, TimeSpan.Zero);
            worker.RequestTrim(cache).ShouldBeTrue();
            await WaitForPass(worker, first);

            var returns = churn.ReturnsSince(cache, DateTime.MinValue);
            returns.Count.ShouldBe(1);
            returns[0].Entry.ShouldBe("a.bin");
            var sizing = worker.GetStatus().Caches[0].Sizing;
            sizing.ShouldNotBeNull();
            sizing.Verdict.ShouldBe(SizingVerdict.NotEnoughHistory);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }
}
