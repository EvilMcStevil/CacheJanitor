using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CacheJanitor.Tests;

[TestClass]
public class ChurnStoreTests
{
    private const string Cache = @"C:\caches\one";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string _db = "";
    private ChurnStore? _store;

    [TestInitialize]
    public void Init() => _db = Path.Combine(Path.GetTempPath(), $"churn-{Guid.NewGuid():N}.db");

    [TestCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private ChurnStore Open() => _store = new ChurnStore(NullLogger.Instance, _db);

    private static EvictionRecord Ev(string entry, long bytes, DateTime at, DateTime? lastUsed = null) =>
        new() { Entry = entry, Bytes = bytes, Utc = at, LastUsedUtc = lastUsed ?? at.AddDays(-1) };

    private static CacheTrimmer.CreatedEntry New(string entry, long bytes, DateTime at) => new(entry, bytes, at);

    private static readonly IReadOnlyList<EvictionRecord> NoEvictions = [];
    private static readonly IReadOnlyList<CacheTrimmer.CreatedEntry> NoCreated = [];

    [TestMethod]
    public void Return_IsMatchedWithReuseDistance()
    {
        var store = Open();
        store.RecordPass(Cache, 1000, T0, [Ev("a", 100, T0), Ev("b", 50, T0)], NoCreated);
        store.RecordPass(Cache, 1000, T0.AddHours(1), [Ev("c", 30, T0.AddHours(1))], NoCreated);
        int matched = store.RecordPass(Cache, 1000, T0.AddHours(2), NoEvictions, [New("a", 80, T0.AddHours(2))]);

        matched.ShouldBe(1);
        var returns = store.ReturnsSince(Cache, T0);
        returns.Count.ShouldBe(1);
        var r = returns[0];
        r.Entry.ShouldBe("a");
        r.ReturnedBytes.ShouldBe(80);
        r.MaxBytesAtEviction.ShouldBe(1000);
        r.ReuseDistance.ShouldBe(80);
        r.NeededMaxBytes.ShouldBe(1080);
        r.GoneFor.ShouldBe(TimeSpan.FromHours(2));
    }

    [TestMethod]
    public void EntryThatWasNeverEvicted_IsNotAReturn()
    {
        var store = Open();
        store.RecordPass(Cache, 1000, T0, NoEvictions, [New("fresh", 10, T0)]).ShouldBe(0);
        store.ReturnsSince(Cache, T0.AddDays(-1)).ShouldBeEmpty();
    }

    [TestMethod]
    public void Return_MatchesIgnoringCase()
    {
        var store = Open();
        store.RecordPass(Cache, 1000, T0, [Ev("Sub/A.bin", 100, T0)], NoCreated);
        store.RecordPass(Cache, 1000, T0.AddHours(1), NoEvictions, [New("sub/a.bin", 100, T0.AddHours(1))]).ShouldBe(1);
        store.ReturnsSince(Cache, T0).Count.ShouldBe(1);
    }

    [TestMethod]
    public void SecondReturnOfSameEntry_NeedsASecondEviction()
    {
        var store = Open();
        store.RecordPass(Cache, 1000, T0, [Ev("a", 100, T0)], NoCreated);
        store.RecordPass(Cache, 1000, T0.AddHours(1), NoEvictions, [New("a", 100, T0.AddHours(1))]).ShouldBe(1);
        store.RecordPass(Cache, 1000, T0.AddHours(2), NoEvictions, [New("a", 100, T0.AddHours(2))]).ShouldBe(0);
        store.ReturnsSince(Cache, T0).Count.ShouldBe(1);
    }

    [TestMethod]
    public void LastPassAndOpenRows_SurviveReopening()
    {
        var first = Open();
        first.RecordPass(Cache, 1000, T0, [Ev("a", 100, T0)], NoCreated);
        first.Dispose();
        SqliteConnection.ClearAllPools();

        var second = Open();
        second.LastPassUtc(Cache).ShouldBe(T0);
        second.RecordPass(Cache, 1000, T0.AddHours(1), NoEvictions, [New("a", 100, T0.AddHours(1))]).ShouldBe(1);
    }

    [TestMethod]
    public void RecordingSince_IsFirstSeenAndStable()
    {
        var store = Open();
        var first = store.RecordingSinceUtc(Cache);
        first.ShouldBeInRange(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Thread.Sleep(20);
        store.RecordingSinceUtc(Cache).ShouldBe(first);
        store.LastPassUtc(Cache).ShouldBeNull();
    }

    [TestMethod]
    public void Prune_DropsRowsOlderThan180Days_AndEnforcesTheCap()
    {
        var store = Open();
        var now = T0.AddDays(300);
        var old = now.AddDays(-200);

        store.RecordPass(Cache, 1000, old, [Ev("stale", 10, old), Ev("returned", 10, old)], NoCreated);
        store.RecordPass(Cache, 1000, now.AddDays(-10), NoEvictions, [New("returned", 10, now.AddDays(-10))]).ShouldBe(1);
        store.Prune(now);
        store.ReturnsSince(Cache, old).Select(r => r.Entry).ShouldBe(["returned"]);

        const string capped = @"C:\caches\capped";
        var evictions = Enumerable.Range(0, 5).Select(i => Ev($"e{i}", 1, now.AddDays(-5).AddHours(i))).ToList();
        store.RecordPass(capped, 1000, now.AddDays(-5), evictions, NoCreated);
        var created = Enumerable.Range(0, 5).Select(i => New($"e{i}", 1, now.AddDays(-1))).ToList();
        store.RecordPass(capped, 1000, now.AddDays(-1), NoEvictions, created).ShouldBe(5);
        store.ReturnsSince(capped, now.AddDays(-2)).Count.ShouldBe(5);

        store.Prune(now, maxRowsPerCache: 3);
        store.ReturnsSince(capped, now.AddDays(-2)).Select(r => r.Entry).OrderBy(e => e).ShouldBe(["e2", "e3", "e4"]);
    }

    [TestMethod]
    public void ManyEvictions_RecordQuickly()
    {
        var store = Open();
        var evictions = Enumerable.Range(0, 20_000).Select(i => Ev($"dir/file{i}.bin", 1000, T0)).ToList();
        var sw = Stopwatch.StartNew();
        store.RecordPass(Cache, 1_000_000, T0, evictions, NoCreated);
        sw.Stop();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void UnusableDatabase_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"churn-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            _store = new ChurnStore(NullLogger.Instance, dir);
            _store.RecordPass(Cache, 1000, T0, [Ev("a", 1, T0)], NoCreated).ShouldBe(0);
            _store.LastPassUtc(Cache).ShouldBeNull();
            _store.ReturnsSince(Cache, T0).ShouldBeEmpty();
            Should.NotThrow(() => _store.Prune(T0));
        }
        finally
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
