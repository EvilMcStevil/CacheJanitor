using System.Windows;
using System.Windows.Media;

namespace CacheJanitor.Tray.Charts;

/// <summary>How the cache's bytes are spread over last-used age, one horizontal bar per age bucket.</summary>
public sealed class AgeChart : ChartBase
{
    private const double LabelWidth = 86, ValueWidth = 150, RowHeight = 26, BarHeight = 12;

    private long[] _bytes = [];
    private long[] _entries = [];
    private string _entryNoun = "files";

    public void SetData(long[] bytes, long[] entries, bool topLevel)
    {
        _bytes = bytes;
        _entries = entries;
        _entryNoun = topLevel ? "entries" : "files";
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, AgeBuckets.Count * RowHeight);

    protected override void OnRender(DrawingContext dc)
    {
        long total = _bytes.Sum();
        if (_bytes.Length != AgeBuckets.Count || total == 0)
        {
            DrawEmpty(dc, "Nothing in the cache yet.");
            return;
        }
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        double barArea = Math.Max(20, ActualWidth - LabelWidth - ValueWidth);
        long max = _bytes.Max();
        int hover = Mouse is { } m ? (int)(m.Y / RowHeight) : -1;

        for (int i = 0; i < AgeBuckets.Count; i++)
        {
            double y = i * RowHeight;
            if (i == hover)
                dc.DrawRoundedRectangle(ChartTheme.Grid, null, new Rect(0, y, ActualWidth, RowHeight), 4, 4);

            var label = Text(AgeBuckets.Labels[i], 12, ChartTheme.InkSecondary);
            dc.DrawText(label, new Point(6, y + (RowHeight - label.Height) / 2));

            double len = max == 0 ? 0 : _bytes[i] / (double)max * barArea;
            double by = y + (RowHeight - BarHeight) / 2;
            dc.DrawLine(Pen(ChartTheme.Baseline, 1), new Point(Snap(LabelWidth), by - 2), new Point(Snap(LabelWidth), by + BarHeight + 2));
            if (len >= 1)
            {
                // Rounded at the data end only; the baseline end is square.
                var g = new StreamGeometry();
                double r = Math.Min(4, len / 2);
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(LabelWidth, by), true, true);
                    c.LineTo(new Point(LabelWidth + len - r, by), true, false);
                    c.ArcTo(new Point(LabelWidth + len, by + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    c.LineTo(new Point(LabelWidth + len, by + BarHeight - r), true, false);
                    c.ArcTo(new Point(LabelWidth + len - r, by + BarHeight), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    c.LineTo(new Point(LabelWidth, by + BarHeight), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(ChartTheme.Series1, null, g);
            }

            var value = Text($"{Fmt.Size(_bytes[i])}  ·  {Fmt.Percent(_bytes[i] / (double)total)}", 12, ChartTheme.Ink);
            dc.DrawText(value, new Point(LabelWidth + len + 8, y + (RowHeight - value.Height) / 2));
        }

        if (hover >= 0 && hover < AgeBuckets.Count)
        {
            DrawTooltip(dc, new Point(Mouse!.Value.X, hover * RowHeight + RowHeight / 2), $"Last used {AgeBuckets.Labels[hover]} ago",
            [
                (null, "Size", Fmt.Size(_bytes[hover])),
                (null, char.ToUpper(_entryNoun[0]) + _entryNoun[1..], Fmt.Count(_entries[hover])),
                (null, "Share of cache", Fmt.Percent(_bytes[hover] / (double)total)),
            ]);
        }
    }
}
