using Microsoft.Extensions.Logging.Abstractions;

namespace CacheJanitor.Tests;

[TestClass]
public sealed class CoreTests
{
    [TestMethod]
    [DataRow("750MB", 750L << 20)]
    [DataRow("1.5TB", 3L << 39)]
    [DataRow("50 GiB", 50L << 30)]
    [DataRow("4096", 4096L)]
    public void SizeParser_Parses(string text, long bytes) => SizeParser.Parse(text).ShouldBe(bytes);

    [TestMethod]
    [DataRow("")]
    [DataRow("lots")]
    [DataRow("1.2.3GB")]
    [DataRow("0")]
    public void SizeParser_RejectsNonsense(string text) => SizeParser.TryParse(text, out _).ShouldBeFalse();

    [TestMethod]
    [DataRow(@"C:\")]
    [DataRow(@"C:\Windows")]
    [DataRow(@"C:\Windows\Temp")]
    [DataRow(@"C:\Program Files\Thing")]
    [DataRow(@"C:\Users")]
    [DataRow(@"relative\path")]
    [DataRow(@"\\server\share")]
    public void PathGuard_RefusesDangerousFolders(string path) => PathGuard.Check(path).ShouldNotBeNull();

    [TestMethod]
    [DataRow(@"D:\ci-cache")]
    [DataRow(@"C:\ci\downloads")]
    [DataRow(@"\\server\share\cache")]
    public void PathGuard_AllowsOrdinaryFolders(string path) => PathGuard.Check(path).ShouldBeNull();

    [TestMethod]
    public void Config_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cj-{Guid.NewGuid():N}.json");
        try
        {
            var o = new JanitorOptions
            {
                ScanIntervalMinutes = 7,
                Caches = [new CacheFolderOptions { Path = @"D:\c", MaxSize = "5GB", Unit = EvictionUnit.TopLevelEntry, Exclude = ["**/*.lock"] }],
            };
            ConfigFile.Save(path, o);
            File.ReadAllText(path).ShouldContain("\"TopLevelEntry\"");
            File.ReadAllText(path).ShouldNotContain("MaxBytes");
            var back = ConfigFile.Load(path);
            back.ScanIntervalMinutes.ShouldBe(7);
            back.Caches.Single().Unit.ShouldBe(EvictionUnit.TopLevelEntry);
            back.Caches.Single().Exclude.ShouldBe(["**/*.lock"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Config_Validate_FlagsNestedAndBadSizes()
    {
        var errors = ConfigFile.Validate(new JanitorOptions
        {
            Caches =
            [
                new CacheFolderOptions { Path = @"D:\cache", MaxSize = "10GB" },
                new CacheFolderOptions { Path = @"D:\cache\inner", MaxSize = "big" },
            ],
        });
        errors.ShouldContain(e => e.Contains("is inside"));
        errors.ShouldContain(e => e.Contains("'big'"));
    }

    [TestMethod]
    public void Stats_EstimatesIngressAndMergesSlots()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cj-stats-{Guid.NewGuid():N}.json");
        try
        {
            var store = new StatsStore(NullLogger.Instance, file);
            var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            store.Record(@"D:\c", Result(before: 100, after: 100), t0);
            store.Record(@"D:\c", Result(before: 180, after: 90, evicted: 2), t0.AddMinutes(1));
            store.Record(@"D:\c", Result(before: 120, after: 120), t0.AddMinutes(10));

            var s = new CacheStatus();
            store.Fill(@"D:\c", s);
            s.Totals.IngressBytes.ShouldBe(80 + 30);
            s.Totals.EvictedBytes.ShouldBe(90);
            s.Totals.Passes.ShouldBe(3);
            s.History.Count.ShouldBe(2);
            s.History[0].IngressBytes.ShouldBe(80);
            s.History[0].Bytes.ShouldBe(90);

            store.SaveIfDirty();
            var reloaded = new StatsStore(NullLogger.Instance, file);
            var s2 = new CacheStatus();
            reloaded.Fill(@"d:\C\", s2);
            s2.Totals.Passes.ShouldBe(3);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static CacheTrimmer.Result Result(long before, long after, int evicted = 0) =>
        new(before, after, evicted, 0, 1, 1, null, new long[5], new long[5], [], TimeSpan.Zero, [], 0, []);
}
