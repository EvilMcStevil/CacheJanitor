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

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out after 10 seconds waiting for {what}.");
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
            await WaitUntil(() => !File.Exists(Path.Combine(cache, "a.bin")) && worker.GetStatus().Caches[0].Sizing is not null, "the first pass to evict a.bin and publish sizing advice");

            MakeFile(cache, "a.bin", 40, TimeSpan.Zero);
            worker.RequestTrim(cache).ShouldBeTrue();
            await WaitUntil(() => churn.ReturnsSince(cache, DateTime.MinValue).Count == 1, "the re-created a.bin to be recorded as a return");

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
