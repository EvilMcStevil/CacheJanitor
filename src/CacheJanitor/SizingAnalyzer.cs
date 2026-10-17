namespace CacheJanitor;

/// <summary>Everything <see cref="SizingAnalyzer"/> needs to advise on one cache.</summary>
public sealed record SizingInput(
    DateTime NowUtc,
    long MaxBytes,
    long CurrentBytes,
    DateTime RecordingSinceUtc,
    IReadOnlyList<ChurnReturn> Returns,
    long UntouchedBytes,
    IReadOnlyList<CacheTrimmer.LruPoint> Profile,
    bool? LastAccessEnabled);

/// <summary>
/// Turns a cache's return history and current contents into <see cref="SizingAdvice"/>.
/// The figures are an estimate, not a simulation: a limit L is taken to avoid every thrash whose
/// <see cref="ChurnReturn.NeededMaxBytes"/> is at most L, ignoring that the avoided re-downloads
/// would themselves not have taken space.
/// </summary>
public static class SizingAnalyzer
{
    private const long GiB = 1L << 30;
    private const long SmallFileBytes = 1L << 20;
    private const long TooSmallMinBytes = GiB;
    private const double TooSmallMinFraction = 0.05;
    private const double TooSmallPeriodFraction = 0.2;
    private const double SuggestedAvoidedFraction = 0.8;
    private const double ShrinkMinFraction = 0.2;
    private const int CurvePoints = 12;
    private const int TopReturnedCount = 10;
    private static readonly TimeSpan ThrashWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan AnalysisWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan TooSmallMinHistory = TimeSpan.FromDays(14);
    private static readonly TimeSpan ShrinkMinHistory = TimeSpan.FromDays(90);
    private static readonly TimeSpan StaleLastUse = TimeSpan.FromDays(30);

    public static SizingAdvice Analyze(SizingInput input)
    {
        var history = input.NowUtc - input.RecordingSinceUtc;
        var windowStart = input.NowUtc - AnalysisWindow;
        var window = input.Returns.Where(r => r.ReturnedUtc >= windowStart && r.ReturnedUtc <= input.NowUtc).ToList();
        var thrash = window.Where(r => r.GoneFor <= ThrashWindow).ToList();
        var counting = thrash.Where(r => r.NeededMaxBytes > input.MaxBytes).ToList();
        double weeks = Math.Max(Math.Min(AnalysisWindow.TotalDays, history.TotalDays), 1) / 7;

        long thrashBytes = thrash.Sum(r => r.ReturnedBytes);
        long staleBytes = thrash.Where(r => r.EvictedUtc - r.LastUsedUtc >= StaleLastUse).Sum(r => r.ReturnedBytes);
        var small = thrash.Where(r => r.ReturnedBytes < SmallFileBytes).ToList();

        var advice = new SizingAdvice
        {
            RecordingSinceUtc = input.RecordingSinceUtc,
            ThrashBytes = thrashBytes,
            ThrashEntries = thrash.Count,
            SmallThrashBytes = small.Sum(r => r.ReturnedBytes),
            SmallThrashEntries = small.Count,
            MedianGoneHours = Median(thrash.Select(r => r.GoneFor.TotalHours)),
            BreakBytes = window.Where(r => r.GoneFor > ThrashWindow).Sum(r => r.ReturnedBytes),
            UntouchedBytes = input.UntouchedBytes,
            LastAccessUnreliable = thrashBytes > 0 && staleBytes * 2 >= thrashBytes,
            TopReturned = TopReturned(window),
        };

        long countingBytes = counting.Sum(r => r.ReturnedBytes);
        bool thrashWorthMentioning = countingBytes > TooSmallMinBytes && countingBytes > input.MaxBytes * TooSmallMinFraction;
        int periods = counting.GroupBy(r => (int)((input.NowUtc - r.ReturnedUtc) / ThrashWindow))
            .Count(g => g.Sum(r => r.ReturnedBytes) >= countingBytes * TooSmallPeriodFraction);
        long? shrinkTo = history >= ShrinkMinHistory && !thrashWorthMentioning
            && input.UntouchedBytes >= input.MaxBytes * ShrinkMinFraction
            ? Math.Max(
                Tidy(input.MaxBytes - input.UntouchedBytes, up: false),
                Tidy(input.CurrentBytes - input.UntouchedBytes, up: true))
            : null;
        bool shrinkable = shrinkTo < input.MaxBytes;

        if (history < TooSmallMinHistory)
        {
            advice.Verdict = SizingVerdict.NotEnoughHistory;
        }
        else if (thrashWorthMentioning && periods >= 2)
        {
            advice.Verdict = SizingVerdict.TooSmall;
            advice.SuggestedMaxBytes = Tidy(AvoidingLimit(counting, countingBytes), up: true);
        }
        else if (shrinkable && input.LastAccessEnabled != false)
        {
            advice.Verdict = SizingVerdict.CouldBeSmaller;
            advice.SuggestedMaxBytes = shrinkTo;
        }
        else
        {
            advice.Verdict = SizingVerdict.AboutRight;
            advice.ShrinkWithheld = shrinkable;
        }
        advice.Curve = BuildCurve(input, counting, weeks, advice.SuggestedMaxBytes);
        return advice;
    }

    /// <summary>Rounds to a tidy limit: whole GiB below 20 GiB, multiples of 5 GiB from there up.</summary>
    internal static long Tidy(long bytes, bool up)
    {
        bytes = Math.Max(bytes, 0);
        long step = bytes < 20 * GiB ? GiB : 5 * GiB;
        long down = bytes / step * step;
        return up && down < bytes ? down + step : down;
    }

    /// <summary>The smallest needed limit that avoids at least 80% of the counting thrash bytes.</summary>
    private static long AvoidingLimit(List<ChurnReturn> counting, long countingBytes)
    {
        long avoided = 0;
        foreach (var r in counting.OrderBy(r => r.NeededMaxBytes))
        {
            avoided += r.ReturnedBytes;
            if (avoided >= countingBytes * SuggestedAvoidedFraction)
                return r.NeededMaxBytes;
        }
        return counting.Max(r => r.NeededMaxBytes);
    }

    /// <summary>
    /// Above the current limit, weekly thrash still counting at each point; confident up to the largest needed limit seen.
    /// Below it, the counting thrash plus the recently used bytes among the oldest bytes the cache holds beyond that
    /// limit, read from the LRU profile; never confident, since it relies on last-used times rather than eviction
    /// evidence. The points run from half the limit to twice it, or to just past <paramref name="suggested"/> when
    /// that is higher.
    /// </summary>
    private static List<SizingPoint> BuildCurve(SizingInput input, List<ChurnReturn> counting, double weeks, long? suggested)
    {
        long max = input.MaxBytes;
        long last = suggested > 2 * max ? (long)(suggested.Value * 1.1) : 2 * max;
        long first = max / 2, span = last - first;
        long largestNeeded = counting.Count > 0 ? counting.Max(r => r.NeededMaxBytes) : max;
        long countingWeekly = (long)(counting.Sum(r => r.ReturnedBytes) / weeks);

        return Enumerable.Range(0, CurvePoints)
            .Select(i => first + (long)((double)span * i / (CurvePoints - 1)))
            .Append(max)
            .Distinct()
            .Order()
            .Select(limit => limit >= max
                ? new SizingPoint
                {
                    MaxBytes = limit,
                    WeeklyThrashBytes = (long)(counting.Where(r => r.NeededMaxBytes > limit).Sum(r => r.ReturnedBytes) / weeks),
                    Confident = limit == max || limit <= largestNeeded,
                }
                : new SizingPoint
                {
                    MaxBytes = limit,
                    WeeklyThrashBytes = countingWeekly + RecentBytesWithin(input.Profile, Math.Max(0, input.CurrentBytes - limit)),
                    Confident = false,
                })
            .ToList();
    }

    /// <summary>Recently used bytes among the oldest <paramref name="bytes"/> bytes, interpolated between profile points.</summary>
    private static long RecentBytesWithin(IReadOnlyList<CacheTrimmer.LruPoint> profile, long bytes)
    {
        long prevBytes = 0, prevRecent = 0;
        foreach (var p in profile)
        {
            if (p.Bytes >= bytes)
            {
                if (p.Bytes == prevBytes) return p.RecentBytes;
                double t = (double)(bytes - prevBytes) / (p.Bytes - prevBytes);
                return prevRecent + (long)((p.RecentBytes - prevRecent) * t);
            }
            (prevBytes, prevRecent) = (p.Bytes, p.RecentBytes);
        }
        return prevRecent;
    }

    private static List<ReturnedEntry> TopReturned(List<ChurnReturn> window) =>
        window.GroupBy(r => r.Entry, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.MaxBy(r => r.ReturnedUtc)!;
                return new ReturnedEntry
                {
                    Entry = latest.Entry,
                    Bytes = latest.ReturnedBytes,
                    Returns = g.Count(),
                    TotalBytes = g.Sum(r => r.ReturnedBytes),
                };
            })
            .OrderByDescending(e => e.TotalBytes)
            .Take(TopReturnedCount)
            .ToList();

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return null;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
