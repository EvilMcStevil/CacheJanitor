using CacheJanitor.Tray;

namespace CacheJanitor.Tests;

[TestClass]
public class TrayTextTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [TestMethod]
    public void Tooltip_AppendsTagsOnlyWhereThereIsAdvice()
    {
        var text = TrayIcon.TooltipText([("NuGet packages", "96% of 20 GB", "↑ 25 GB"), ("sccache", "88% of 50 GB", null)]);
        text.ShouldBe("NuGet packages: 96% of 20 GB · ↑ 25 GB\nsccache: 88% of 50 GB");
    }

    [TestMethod]
    public void Tooltip_ShortensNamesBeforeDroppingAdvice()
    {
        var lines = Enumerable.Range(0, 4).Select(i => (new string((char)('a' + i), 30), "96% of 20 GB", (string?)"↑ 25 GB")).ToList();
        var text = TrayIcon.TooltipText(lines);
        text.Length.ShouldBeLessThanOrEqualTo(127);
        text.Split('\n').ShouldAllBe(l => l.EndsWith("↑ 25 GB"));
    }

    [TestMethod]
    public void Tooltip_FallsBackToTruncationWhenNamesCannotShrinkEnough()
    {
        var lines = Enumerable.Range(0, 12).Select(i => ($"Folder number {i}", "96% of 20 GB", (string?)"↑ 25 GB")).ToList();
        var text = TrayIcon.TooltipText(lines);
        text.Length.ShouldBeLessThanOrEqualTo(127);
        text.ShouldEndWith("…");
    }

    [TestMethod]
    public void AdviceTag_FollowsVerdict()
    {
        TrayIcon.AdviceTag(new SizingAdvice { Verdict = SizingVerdict.TooSmall, SuggestedMaxBytes = 25 * Gb }).ShouldBe("↑ 25 GB");
        TrayIcon.AdviceTag(new SizingAdvice { Verdict = SizingVerdict.CouldBeSmaller, SuggestedMaxBytes = 5 * Gb }).ShouldBe("↓ 5 GB");
        TrayIcon.AdviceTag(new SizingAdvice { Verdict = SizingVerdict.AboutRight }).ShouldBeNull();
        TrayIcon.AdviceTag(null).ShouldBeNull();
    }
}
