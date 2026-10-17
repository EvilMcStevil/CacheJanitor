using System.Windows;
using System.Windows.Media;

namespace CacheJanitor.Tray.Charts;

/// <summary>Bytes added and bytes evicted per time bucket, as paired bars on one byte axis.</summary>
public sealed class ChurnChart : ChartBase
{
    private const double Left = 64, Right = 12, Top = 30, Bottom = 24;

    private sealed record Bucket(DateTime Start, long Added, long Evicted, int Evictions);

    private List<Bucket> _buckets = [];
    private TimeSpan _bucketWidth = TimeSpan.FromHours(1);
    private DateTime _start;

    public void SetData(IReadOnlyList<HistorySample> samples, TimeSpan range)
    {
        _bucketWidth = range > TimeSpan.FromDays(2) ? TimeSpan.FromHours(6) : TimeSpan.FromHours(1);
        var now = DateTime.UtcNow;
        var lastStart = new DateTime(now.Ticks - now.Ticks % _bucketWidth.Ticks, DateTimeKind.Utc);
        int count = (int)Math.Ceiling(range / _bucketWidth);
        _start = lastStart - _bucketWidth * (count - 1);
        var buckets = Enumerable.Range(0, count).Select(i => new Bucket(_start + _bucketWidth * i, 0, 0, 0)).ToList();
        foreach (var s in samples)
        {
            int i = (int)Math.Floor((s.Utc - _start) / _bucketWidth);
            if (i < 0 || i >= count) continue;
            var b = buckets[i];
            buckets[i] = b with { Added = b.Added + s.IngressBytes, Evicted = b.Evicted + s.EvictedBytes, Evictions = b.Evictions + s.Evictions };
        }
        _buckets = buckets;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double w = ActualWidth - Left - Right, h = ActualHeight - Top - Bottom;
        if (_buckets.Count == 0 || w < 40 || h < 30) return;

        long added = _buckets.Sum(b => b.Added), evicted = _buckets.Sum(b => b.Evicted);
        DrawLegend(dc, [(ChartTheme.Series1, "Added", Fmt.Size(added)), (ChartTheme.Series2, "Evicted", Fmt.Size(evicted))]);

        double max = _buckets.Max(b => Math.Max(b.Added, b.Evicted));
        if (max <= 0)
        {
            var t = Text("No downloads or evictions in this period.", 12, ChartTheme.InkMuted);
            dc.DrawText(t, new Point(Left + (w - t.Width) / 2, Top + (h - t.Height) / 2));
            dc.DrawLine(Pen(ChartTheme.Baseline, 1), new Point(Left, Snap(Top + h)), new Point(Left + w, Snap(Top + h)));
            return;
        }

        double step = NiceByteStep(max * 1.05, 4);
        double yMax = Math.Ceiling(max * 1.05 / step) * step;
        double Y(double v) => Top + h - v / yMax * h;
        for (double v = 0; v <= yMax + 1; v += step)
        {
            double y = Snap(Y(v));
            dc.DrawLine(Pen(v == 0 ? ChartTheme.Baseline : ChartTheme.Grid, 1), new Point(Left, y), new Point(Left + w, y));
            var label = Text(Fmt.Size((long)v), 11, ChartTheme.InkMuted);
            dc.DrawText(label, new Point(Left - 8 - label.Width, y - label.Height / 2));
        }

        double slot = w / _buckets.Count;
        double gap = 2;
        double barW = Math.Max(1.5, Math.Min(18, (slot - 4 - gap) / 2));
        int hover = Mouse is { } m && m.X >= Left && m.X < Left + w && m.Y >= Top && m.Y <= Top + h
            ? (int)((m.X - Left) / slot) : -1;

        for (int i = 0; i < _buckets.Count; i++)
        {
            var b = _buckets[i];
            double cx = Left + slot * (i + 0.5);
            if (i == hover)
                dc.DrawRoundedRectangle(ChartTheme.Grid, null, new Rect(Left + slot * i, Top, slot, h), 4, 4);
            Bar(dc, cx - gap / 2 - barW, barW, Y(b.Added), Top + h, ChartTheme.Series1);
            Bar(dc, cx + gap / 2, barW, Y(b.Evicted), Top + h, ChartTheme.Series2);
        }

        // X labels at every 4th hour, or at each day.
        bool daily = _bucketWidth >= TimeSpan.FromHours(6);
        for (int i = 0; i < _buckets.Count; i++)
        {
            var local = _buckets[i].Start.ToLocalTime();
            bool show = daily ? local.Hour < _bucketWidth.TotalHours : local.Hour % 4 == 0;
            if (!show) continue;
            var label = Text(daily ? local.ToString("ddd d") : local.ToString("HH:mm"), 11, ChartTheme.InkMuted);
            double x = Left + slot * i + (daily ? 0 : slot / 2 - label.Width / 2);
            if (x + label.Width < Left + w + 8)
                dc.DrawText(label, new Point(x, Top + h + 6));
        }

        if (hover >= 0)
        {
            var b = _buckets[hover];
            var startLocal = b.Start.ToLocalTime();
            var title = $"{Fmt.LocalTime(b.Start)} – {(startLocal + _bucketWidth):t}";
            DrawTooltip(dc, new Point(Left + slot * (hover + 0.5), Top + h / 2), title,
            [
                (ChartTheme.Series1, "Added", Fmt.Size(b.Added)),
                (ChartTheme.Series2, "Evicted", Fmt.Size(b.Evicted)),
                (null, "Entries evicted", Fmt.Count(b.Evictions)),
            ]);
        }
    }

    private void DrawLegend(DrawingContext dc, IReadOnlyList<(Brush Brush, string Label, string Value)> items)
    {
        double x = Left;
        foreach (var (brush, label, value) in items)
        {
            dc.DrawRoundedRectangle(brush, null, new Rect(x, 5, 10, 10), 2, 2);
            var l = Text(label, 12, ChartTheme.InkSecondary);
            dc.DrawText(l, new Point(x + 16, 2));
            var v = Text(value, 12, ChartTheme.Ink, bold: true);
            dc.DrawText(v, new Point(x + 16 + l.Width + 6, 2));
            x += 16 + l.Width + 6 + v.Width + 24;
        }
    }

    /// <summary>A bar anchored to the baseline with 4px rounding on its top corners only.</summary>
    private static void Bar(DrawingContext dc, double x, double width, double top, double baseline, Brush brush)
    {
        double height = baseline - top;
        if (height < 0.5) return;
        double r = Math.Min(4, Math.Min(width / 2, height));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, baseline), true, true);
            c.LineTo(new Point(x, top + r), true, false);
            c.ArcTo(new Point(x + r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x + width - r, top), true, false);
            c.ArcTo(new Point(x + width, top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x + width, baseline), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }
}
