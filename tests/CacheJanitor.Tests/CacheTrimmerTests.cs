using Microsoft.Extensions.Logging.Abstractions;

namespace CacheJanitor.Tests;

[TestClass]
public sealed class CacheTrimmerTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cj-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string MakeFile(string relative, int kb, TimeSpan age)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[kb * 1024]);
        var t = DateTime.UtcNow - age;
        File.SetCreationTimeUtc(path, t);
        File.SetLastWriteTimeUtc(path, t);
        File.SetLastAccessTimeUtc(path, t);
        return path;
    }

    private CacheTrimmer Trimmer(string maxSize, EvictionUnit unit = EvictionUnit.File, params string[] exclude) =>
        new(new CacheFolderOptions
        {
            Path = _root,
            MaxSize = maxSize,
            TrimToPercent = 50,
            MinAgeMinutes = 10,
            Unit = unit,
            Exclude = exclude.ToList(),
        }, NullLogger.Instance);

    [TestMethod]
    public async Task UnderLimit_EvictsNothing()
    {
        MakeFile("a.bin", 10, TimeSpan.FromDays(1));
        var r = await Trimmer("1MB").TrimAsync();
        r.Evicted.ShouldBe(0);
        r.BytesAfter.ShouldBe(10 * 1024);
        r.Files.ShouldBe(1);
    }

    [TestMethod]
    public async Task OverLimit_EvictsOldestFirstDownToTarget()
    {
        var oldest = MakeFile("old.bin", 40, TimeSpan.FromDays(3));
        var middle = MakeFile("sub/mid.bin", 40, TimeSpan.FromDays(2));
        var newest = MakeFile("new.bin", 40, TimeSpan.FromDays(1));
        Directory.SetCreationTimeUtc(Path.Combine(_root, "sub"), DateTime.UtcNow - TimeSpan.FromDays(2));
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "sub"), DateTime.UtcNow - TimeSpan.FromDays(2));

        var r = await Trimmer("100KB").TrimAsync(); // target 50KB

        File.Exists(oldest).ShouldBeFalse();
        File.Exists(middle).ShouldBeFalse();
        File.Exists(newest).ShouldBeTrue();
        r.Evicted.ShouldBe(2);
        r.BytesAfter.ShouldBe(40 * 1024);
        r.Evictions.Select(e => e.Entry).ShouldBe(["old.bin", "sub/mid.bin"]);
        Directory.Exists(Path.Combine(_root, "sub")).ShouldBeFalse("emptied folders are removed");
    }

    [TestMethod]
    public async Task RecentFiles_AreNeverEvicted()
    {
        var fresh = MakeFile("fresh.bin", 200, TimeSpan.FromMinutes(1));
        var r = await Trimmer("100KB").TrimAsync();
        File.Exists(fresh).ShouldBeTrue();
        r.Evicted.ShouldBe(0);
    }

    [TestMethod]
    public async Task ExcludedFiles_AreSkipped()
    {
        var locked = MakeFile("pkg/x.lock", 80, TimeSpan.FromDays(5));
        var other = MakeFile("pkg/x.bin", 80, TimeSpan.FromDays(4));
        await Trimmer("100KB", EvictionUnit.File, "**/*.lock").TrimAsync();
        File.Exists(locked).ShouldBeTrue();
        File.Exists(other).ShouldBeFalse();
    }

    [TestMethod]
    public async Task OpenFiles_AreCountedAsInUse()
    {
        var open = MakeFile("open.bin", 200, TimeSpan.FromDays(5));
        using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = await Trimmer("100KB").TrimAsync();
            r.SkippedInUse.ShouldBe(1);
        }
        File.Exists(open).ShouldBeTrue();
    }

    [TestMethod]
    public async Task TopLevelEntry_EvictsWholeFolders()
    {
        MakeFile("toolA/bin/a.exe", 30, TimeSpan.FromDays(9));
        MakeFile("toolA/lib/a.dll", 10, TimeSpan.FromDays(1)); // newest file decides the folder's age
        MakeFile("toolB/b.exe", 70, TimeSpan.FromDays(5));
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "toolA"), DateTime.UtcNow - TimeSpan.FromDays(9));
        Directory.SetCreationTimeUtc(Path.Combine(_root, "toolA"), DateTime.UtcNow - TimeSpan.FromDays(9));
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "toolB"), DateTime.UtcNow - TimeSpan.FromDays(5));
        Directory.SetCreationTimeUtc(Path.Combine(_root, "toolB"), DateTime.UtcNow - TimeSpan.FromDays(5));

        var r = await Trimmer("100KB", EvictionUnit.TopLevelEntry).TrimAsync();

        Directory.Exists(Path.Combine(_root, "toolB")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_root, "toolA")).ShouldBeTrue();
        r.Entries.ShouldBe(1);
        r.Files.ShouldBe(2);
        Directory.EnumerateDirectories(_root, CacheTrimmer.TrashPrefix + "*").ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Disabled_MeasuresWithoutEvicting()
    {
        var f = MakeFile("a.bin", 200, TimeSpan.FromDays(5));
        var trimmer = new CacheTrimmer(new CacheFolderOptions { Path = _root, MaxSize = "100KB", Enabled = false }, NullLogger.Instance);
        var r = await trimmer.TrimAsync();
        File.Exists(f).ShouldBeTrue();
        r.BytesAfter.ShouldBe(200 * 1024);
    }

    [TestMethod]
    public async Task AgeBuckets_AreFilled()
    {
        MakeFile("a", 1, TimeSpan.FromMinutes(30));
        MakeFile("b", 1, TimeSpan.FromHours(5));
        MakeFile("c", 1, TimeSpan.FromDays(3));
        MakeFile("d", 1, TimeSpan.FromDays(60));
        var r = await Trimmer("1GB").TrimAsync();
        r.AgeEntries.ShouldBe([1L, 1L, 1L, 0L, 1L]);
        r.OldestUsedUtc!.Value.ShouldBeLessThan(DateTime.UtcNow - TimeSpan.FromDays(59));
    }

    [TestMethod]
    public async Task Created_ListsEntriesCreatedSinceTheGivenTime()
    {
        MakeFile("old.bin", 10, TimeSpan.FromDays(2));
        MakeFile("new.bin", 10, TimeSpan.FromSeconds(30));   // within the 1-minute allowance
        var r = await Trimmer("1MB").TrimAsync(createdSinceUtc: DateTime.UtcNow);
        r.Created.Select(c => c.RelativePath).ShouldBe(["new.bin"]);
        r.Created[0].Bytes.ShouldBe(10 * 1024);
    }

    [TestMethod]
    public async Task Created_IsEmptyWithoutAThreshold()
    {
        MakeFile("new.bin", 10, TimeSpan.FromSeconds(30));
        (await Trimmer("1MB").TrimAsync()).Created.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task Created_InTopLevelMode_UsesTheFolder()
    {
        MakeFile("pkg/1.0/a.bin", 10, TimeSpan.FromSeconds(30));
        Directory.SetCreationTimeUtc(Path.Combine(_root, "pkg"), DateTime.UtcNow.AddSeconds(-30));
        var r = await Trimmer("1MB", EvictionUnit.TopLevelEntry).TrimAsync(createdSinceUtc: DateTime.UtcNow.AddMinutes(-5));
        r.Created.Select(c => c.RelativePath).ShouldBe(["pkg"]);
    }

    [TestMethod]
    public async Task UntouchedBytes_CountsEntriesUnused90DaysOrMore()
    {
        MakeFile("ancient.bin", 30, TimeSpan.FromDays(100));
        MakeFile("recent.bin", 20, TimeSpan.FromDays(10));
        (await Trimmer("1MB").TrimAsync()).UntouchedBytes.ShouldBe(30 * 1024);
    }

    [TestMethod]
    public async Task Profile_IsCumulativeOldestFirst()
    {
        MakeFile("a.bin", 10, TimeSpan.FromDays(30));
        MakeFile("b.bin", 20, TimeSpan.FromDays(3));
        var p = (await Trimmer("1MB").TrimAsync()).Profile;
        p[^1].ShouldBe(new CacheTrimmer.LruPoint(30 * 1024, 20 * 1024));
        p.Select(x => x.Bytes).ShouldBeInOrder();
    }

    [TestMethod]
    public async Task MissingFolder_Throws()
    {
        var t = new CacheTrimmer(new CacheFolderOptions { Path = Path.Combine(_root, "nope"), MaxSize = "1GB" }, NullLogger.Instance);
        await Should.ThrowAsync<DirectoryNotFoundException>(() => t.TrimAsync());
    }

    [TestMethod]
    [DataRow("**/*.lock", "a/b/c.lock", true)]
    [DataRow("**/*.lock", "c.lock", true)]
    [DataRow("*.lock", "a/c.lock", false)]
    [DataRow("keep", "keep/inner/file", true)]
    [DataRow("k?ep", "kxep", true)]
    [DataRow("keep", "keeper", false)]
    public void Globs(string glob, string path, bool matches) =>
        CacheTrimmer.GlobToRegex(glob).IsMatch(path).ShouldBe(matches);
}
