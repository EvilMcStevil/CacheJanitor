using System.Windows;
using System.Windows.Media;

namespace CacheJanitor.Tray.Charts;

/// <summary>Cache size over time as a line with a light area, against dashed limit and dotted target lines.</summary>
public sealed class SizeChart : ChartBase
{
    private const double Left = 64, Right = 12, Top = 10, Bottom = 24;

    private IReadOnlyList<HistorySample> _samples = [];
    private long _limit, _target;
    private TimeSpan _range = TimeSpan.FromHours(24);

    public void SetData(IReadOnlyList<HistorySample> samples, long limit, long target, TimeSpan range)
    {
        _samples = samples;
        _limit = limit;
        _target = target;
        _range = range;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var end = DateTime.UtcNow;
        var start = end - _range;
        var points = _samples.Where(s => s.Utc >= start - StatsSlot).ToList();
        if (points.Count == 0)
        {
            DrawEmpty(dc, "No history yet — it builds up as the service scans.");
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double w = ActualWidth - Left - Right, h = ActualHeight - Top - Bottom;
        if (w < 40 || h < 40) return;

        double dataMax = Math.Max(_limit, points.Max(p => p.Bytes));
        double step = NiceByteStep(dataMax * 1.08, 4);
        double yMax = Math.Ceiling(dataMax * 1.08 / step) * step;
        double X(DateTime t) => Left + (t - start).Ticks / (double)_range.Ticks * w;
        double Y(double bytes) => Top + h - bytes / yMax * h;

        // Grid and y labels.
        for (double v = 0; v <= yMax + 1; v += step)
        {
            double y = Snap(Y(v));
            dc.DrawLine(Pen(v == 0 ? ChartTheme.Baseline : ChartTheme.Grid, 1), new Point(Left, y), new Point(Left + w, y));
            var label = Text(Fmt.Size((long)v), 11, ChartTheme.InkMuted);
            dc.DrawText(label, new Point(Left - 8 - label.Width, y - label.Height / 2));
        }

        // X labels: every 4 hours for a day, each midnight for a week.
        var local = start.ToLocalTime();
        bool daily = _range > TimeSpan.FromDays(2);
        var tick = daily ? local.Date.AddDays(1) : local.Date.AddHours((local.Hour / 4 + 1) * 4);
        while (tick.ToUniversalTime() < end)
        {
            double x = X(tick.ToUniversalTime());
            var label = Text(daily ? tick.ToString("ddd d") : tick.ToString("HH:mm"), 11, ChartTheme.InkMuted);
            if (x - label.Width / 2 > Left - 4 && x + label.Width / 2 < Left + w + 8)
                dc.DrawText(label, new Point(x - label.Width / 2, Top + h + 6));
            tick = daily ? tick.AddDays(1) : tick.AddHours(4);
        }

        // Size: split into runs wherever the service was not recording.
        dc.PushClip(new RectangleGeometry(new Rect(Left, Top - 4, w, h + 8)));
        foreach (var run in Runs(points))
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(X(run[0].Utc), Y(0)), true, true);
                foreach (var p in run) g.LineTo(new Point(X(p.Utc), Y(p.Bytes)), true, true);
                g.LineTo(new Point(X(run[^1].Utc), Y(0)), true, true);
            }
            area.Freeze();
            dc.DrawGeometry(ChartTheme.Series1Area, null, area);

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(X(run[0].Utc), Y(run[0].Bytes)), false, false);
                foreach (var p in run.Skip(1)) g.LineTo(new Point(X(p.Utc), Y(p.Bytes)), true, true);
            }
            line.Freeze();
            var pen = Pen(ChartTheme.Series1, 2);
            pen = pen.Clone();
            pen.LineJoin = PenLineJoin.Round;
            dc.DrawGeometry(null, pen, line);
            if (run.Count == 1)
                dc.DrawEllipse(ChartTheme.Series1, null, new Point(X(run[0].Utc), Y(run[0].Bytes)), 3, 3);
        }
        dc.Pop();

        // Reference lines, labelled at the right.
        if (_limit > 0)
        {
            DrawReference(dc, Y(_limit), w, "Limit " + Fmt.Size(_limit), new DashStyle([4, 3], 0), ChartTheme.InkSecondary);
            if (_target > 0 && _target < _limit)
                DrawReference(dc, Y(_target), w, "Trims to " + Fmt.Size(_target), new DashStyle([1, 3], 0), ChartTheme.InkMuted, below: true);
        }

        // Hover: nearest sample.
        if (Mouse is { } m && m.X >= Left && m.X <= Left + w)
        {
            var nearest = points.MinBy(p => Math.Abs(X(p.Utc) - m.X))!;
            double x = X(nearest.Utc), y = Y(nearest.Bytes);
            dc.DrawLine(Pen(ChartTheme.InkMuted, 1), new Point(Snap(x), Top), new Point(Snap(x), Top + h));
            dc.DrawEllipse(ChartTheme.Series1, Pen(ChartTheme.Surface, 2), new Point(x, y), 5, 5);
            var rows = new List<(Brush?, string, string)> { (ChartTheme.Series1, "Size", Fmt.Size(nearest.Bytes)) };
            if (_limit > 0) rows.Add((null, "Of limit", Fmt.Percent(nearest.Bytes / (double)_limit)));
            rows.Add((null, "Files", Fmt.Count(nearest.Files)));
            if (nearest.IngressBytes > 0) rows.Add((null, "Added", Fmt.Size(nearest.IngressBytes)));
            if (nearest.EvictedBytes > 0) rows.Add((null, "Evicted", Fmt.Size(nearest.EvictedBytes)));
            DrawTooltip(dc, new Point(x, y), Fmt.LocalTime(nearest.Utc), rows);
        }
    }

    private static readonly TimeSpan StatsSlot = TimeSpan.FromMinutes(5);

    private void DrawReference(DrawingContext dc, double y, double w, string text, DashStyle dash, Brush brush, bool below = false)
    {
        y = Snap(y);
        dc.DrawLine(Pen(brush, 1, dash), new Point(Left, y), new Point(Left + w, y));
        var label = Text(text, 11, brush);
        dc.DrawText(label, new Point(Left + w - label.Width - 2, below ? y + 2 : y - label.Height - 1));
    }

    /// <summary>
    /// Consecutive samples with no gap longer than 75 minutes. A quiet cache is rescanned only hourly, so a
    /// longer gap means the service was not running.
    /// </summary>
    private static IEnumerable<List<HistorySample>> Runs(List<HistorySample> points)
    {
        var run = new List<HistorySample>();
        foreach (var p in points)
        {
            if (run.Count > 0 && p.Utc - run[^1].Utc > TimeSpan.FromMinutes(75))
            {
                yield return run;
                run = new List<HistorySample>();
            }
            run.Add(p);
        }
        if (run.Count > 0) yield return run;
    }
}
