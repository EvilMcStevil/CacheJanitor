namespace CacheJanitor.Tests;

[TestClass]
public class SizingAnalyzerTests
{
    private const long GB = 1L << 30;
    private const long KB = 1L << 10;
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ChurnReturn R(string entry, long returnedBytes, TimeSpan goneFor, TimeSpan returnedAgo,
        long maxAtEviction, long reuseDistance, TimeSpan lastUsedBeforeEviction)
    {
        var returned = Now - returnedAgo;
        var evicted = returned - goneFor;
        return new ChurnReturn(entry, evicted, evicted - lastUsedBeforeEviction, maxAtEviction, returned, returnedBytes, reuseDistance);
    }

    private static ChurnReturn Thrash(string entry, long bytes, double goneDays, double agoDays, long reuseDistance,
        double lastUsedDaysBeforeEviction = 1) =>
        R(entry, bytes, TimeSpan.FromDays(goneDays), TimeSpan.FromDays(agoDays), 20 * GB, reuseDistance,
            TimeSpan.FromDays(lastUsedDaysBeforeEviction));

    private static SizingInput Input(IReadOnlyList<ChurnReturn> returns, double historyDays = 60, long maxBytes = 20 * GB,
        long currentBytes = 19 * GB, long untouched = 0, IReadOnlyList<CacheTrimmer.LruPoint>? profile = null,
        bool? lastAccess = true) =>
        new(Now, maxBytes, currentBytes, Now.AddDays(-historyDays), returns, untouched, profile ?? [], lastAccess);

    private static readonly ChurnReturn[] TwoWeeksOfThrash =
    [
        Thrash("a", 2 * GB, goneDays: 1, agoDays: 3, reuseDistance: 3 * GB),
        Thrash("b", 2 * GB, goneDays: 2, agoDays: 12, reuseDistance: 4 * GB),
    ];

    [TestMethod]
    public void NoHistory_IsNotEnoughHistory()
    {
        var advice = SizingAnalyzer.Analyze(Input(TwoWeeksOfThrash, historyDays: 5));

        advice.Verdict.ShouldBe(SizingVerdict.NotEnoughHistory);
        advice.SuggestedMaxBytes.ShouldBeNull();
        advice.RecordingSinceUtc.ShouldBe(Now.AddDays(-5));
    }

    [TestMethod]
    public void QuietCache_IsAboutRight()
    {
        var advice = SizingAnalyzer.Analyze(Input([], historyDays: 20));

        advice.Verdict.ShouldBe(SizingVerdict.AboutRight);
        advice.SuggestedMaxBytes.ShouldBeNull();
        advice.ThrashBytes.ShouldBe(0);
        advice.MedianGoneHours.ShouldBeNull();
    }

    [TestMethod]
    public void ThrashInTwoWeeks_IsTooSmall()
    {
        var advice = SizingAnalyzer.Analyze(Input(TwoWeeksOfThrash));

        advice.Verdict.ShouldBe(SizingVerdict.TooSmall);
        advice.ThrashBytes.ShouldBe(4 * GB);
        advice.ThrashEntries.ShouldBe(2);
        advice.SuggestedMaxBytes.ShouldBe(25 * GB);
        advice.MedianGoneHours.ShouldBe(36);
        advice.LastAccessUnreliable.ShouldBeFalse();
    }

    [TestMethod]
    public void ThrashInOneWeekOnly_IsNotTooSmall()
    {
        var advice = SizingAnalyzer.Analyze(Input(
        [
            Thrash("a", 2 * GB, goneDays: 1, agoDays: 2, reuseDistance: 3 * GB),
            Thrash("b", 2 * GB, goneDays: 2, agoDays: 2, reuseDistance: 4 * GB),
        ]));

        advice.Verdict.ShouldBe(SizingVerdict.AboutRight);
        advice.ThrashBytes.ShouldBe(4 * GB);
        advice.SuggestedMaxBytes.ShouldBeNull();
    }

    [TestMethod]
    public void SmallThrash_DoesNotMakeItTooSmall()
    {
        var returns = Enumerable.Range(0, 3000)
            .Select(i => Thrash($"f{i}", 300 * KB, goneDays: 1, agoDays: i * 21.0 / 3000, reuseDistance: 2 * GB))
            .ToList();

        var advice = SizingAnalyzer.Analyze(Input(returns));

        advice.Verdict.ShouldNotBe(SizingVerdict.TooSmall);
        advice.SmallThrashEntries.ShouldBe(3000);
        advice.SmallThrashBytes.ShouldBe(3000 * 300 * KB);
        advice.ThrashEntries.ShouldBe(3000);
        advice.ThrashBytes.ShouldBe(3000 * 300 * KB);
    }

    [TestMethod]
    public void LongBreakReturns_AreNotThrash()
    {
        var advice = SizingAnalyzer.Analyze(Input([Thrash("old", 6 * GB, goneDays: 40, agoDays: 2, reuseDistance: 30 * GB)]));

        advice.BreakBytes.ShouldBe(6 * GB);
        advice.ThrashBytes.ShouldBe(0);
        advice.ThrashEntries.ShouldBe(0);
        advice.Verdict.ShouldBe(SizingVerdict.AboutRight);
    }

    [TestMethod]
    public void RaisingTheLimit_StopsAvoidedThrashCounting()
    {
        var advice = SizingAnalyzer.Analyze(Input(TwoWeeksOfThrash, maxBytes: 25 * GB));

        advice.Verdict.ShouldBe(SizingVerdict.AboutRight);
        advice.ThrashBytes.ShouldBe(4 * GB);
        advice.SuggestedMaxBytes.ShouldBeNull();
    }

    [TestMethod]
    public void ReturnedBytes_NotEvictedBytes_AreCounted()
    {
        var advice = SizingAnalyzer.Analyze(Input(
        [
            Thrash("a", 3 * GB, goneDays: 1, agoDays: 3, reuseDistance: 3 * GB),
            Thrash("b", GB / 2, goneDays: 1, agoDays: 4, reuseDistance: 3 * GB),
        ]));

        advice.ThrashBytes.ShouldBe(3 * GB + GB / 2);
        advice.ThrashEntries.ShouldBe(2);
    }

    [TestMethod]
    public void Untouched_GivesCouldBeSmaller_After90Days()
    {
        var advice = SizingAnalyzer.Analyze(Input([], historyDays: 120, untouched: 6 * GB));

        advice.Verdict.ShouldBe(SizingVerdict.CouldBeSmaller);
        advice.SuggestedMaxBytes.ShouldBe(14 * GB);
        advice.UntouchedBytes.ShouldBe(6 * GB);
        advice.ShrinkWithheld.ShouldBeFalse();
    }

    [TestMethod]
    public void Untouched_Before90Days_IsNotCouldBeSmaller()
    {
        var advice = SizingAnalyzer.Analyze(Input([], historyDays: 60, untouched: 6 * GB));

        advice.Verdict.ShouldBe(SizingVerdict.AboutRight);
        advice.SuggestedMaxBytes.ShouldBeNull();
    }

    [TestMethod]
    public void LastAccessOff_WithholdsShrink()
    {
        var advice = SizingAnalyzer.Analyze(Input([], historyDays: 120, untouched: 6 * GB, lastAccess: false));

        advice.Verdict.ShouldBe(SizingVerdict.AboutRight);
        advice.SuggestedMaxBytes.ShouldBeNull();
        advice.ShrinkWithheld.ShouldBeTrue();
    }

    [TestMethod]
    public void OldLastUsed_FlagsUnreliableLastAccess()
    {
        var advice = SizingAnalyzer.Analyze(Input(
        [
            Thrash("stale", 3 * GB, goneDays: 1, agoDays: 3, reuseDistance: 3 * GB, lastUsedDaysBeforeEviction: 45),
            Thrash("fresh", 1 * GB, goneDays: 1, agoDays: 4, reuseDistance: 3 * GB),
        ]));

        advice.LastAccessUnreliable.ShouldBeTrue();
    }

    [TestMethod]
    public void Curve_SpansHalfToDouble_AndDashesBeyondEvidence()
    {
        CacheTrimmer.LruPoint[] profile = [new(5 * GB, 0), new(10 * GB, GB), new(19 * GB, 6 * GB)];
        var advice = SizingAnalyzer.Analyze(Input(TwoWeeksOfThrash, profile: profile));
        var curve = advice.Curve;
        long largestNeeded = 24 * GB;

        curve.Count.ShouldBeGreaterThanOrEqualTo(12);
        curve.Select(p => p.MaxBytes).ShouldBe(curve.Select(p => p.MaxBytes).Order());
        curve[0].MaxBytes.ShouldBeLessThanOrEqualTo(10 * GB);
        curve[^1].MaxBytes.ShouldBeGreaterThanOrEqualTo(40 * GB);
        curve.ShouldContain(p => p.MaxBytes == 20 * GB && p.Confident);
        curve.Where(p => p.MaxBytes < 20 * GB).ShouldAllBe(p => !p.Confident);
        curve.Where(p => p.MaxBytes > largestNeeded).ShouldAllBe(p => !p.Confident);
        curve.Where(p => p.MaxBytes > 20 * GB && p.MaxBytes <= largestNeeded).ShouldAllBe(p => p.Confident);

        var above = curve.Where(p => p.MaxBytes >= 20 * GB).Select(p => p.WeeklyThrashBytes).ToList();
        for (int i = 1; i < above.Count; i++)
            above[i].ShouldBeLessThanOrEqualTo(above[i - 1]);
        above[^1].ShouldBe(0);

        var below = curve.Where(p => p.MaxBytes < 20 * GB).ToList();
        below.ShouldAllBe(p => p.WeeklyThrashBytes >= above[0]);
        below[0].WeeklyThrashBytes.ShouldBeGreaterThan(above[0]);
    }

    [TestMethod]
    public void Curve_WithoutReturns_IsConfidentOnlyAtTheLimit()
    {
        var advice = SizingAnalyzer.Analyze(Input([]));

        advice.Curve.Where(p => p.Confident).Select(p => p.MaxBytes).ShouldBe([20 * GB]);
    }

    [TestMethod]
    public void TopReturned_RanksByTotalBytes_Top10()
    {
        var returns = Enumerable.Range(1, 12)
            .Select(i => Thrash($"e{i}", i * GB / 10, goneDays: 1, agoDays: 3, reuseDistance: GB))
            .Append(Thrash("E1", 2 * GB, goneDays: 1, agoDays: 1, reuseDistance: GB))
            .Append(Thrash("e2", 15 * GB / 10, goneDays: 20, agoDays: 5, reuseDistance: GB))
            .ToList();

        var top = SizingAnalyzer.Analyze(Input(returns)).TopReturned;

        top.Count.ShouldBe(10);
        top.Select(t => t.TotalBytes).ShouldBe(top.Select(t => t.TotalBytes).OrderDescending());
        var e1 = top[0];
        e1.Entry.ShouldBe("E1", StringCompareShould.IgnoreCase);
        e1.Returns.ShouldBe(2);
        e1.Bytes.ShouldBe(2 * GB);
        e1.TotalBytes.ShouldBe(2 * GB + GB / 10);
        var e2 = top.Single(t => t.Entry == "e2");
        e2.Returns.ShouldBe(2);
        e2.Bytes.ShouldBe(2 * GB / 10);
        e2.TotalBytes.ShouldBe(2 * GB / 10 + 15 * GB / 10);
    }

    [TestMethod]
    [DataRow(19.2, true, 20.0)]
    [DataRow(23.0, true, 25.0)]
    [DataRow(14.5, false, 14.0)]
    [DataRow(27.0, false, 25.0)]
    public void Tidy(double gb, bool up, double expectedGb) =>
        SizingAnalyzer.Tidy((long)(gb * GB), up).ShouldBe((long)(expectedGb * GB));
}
