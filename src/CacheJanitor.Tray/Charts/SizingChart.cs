using System.Windows;
using System.Windows.Media;

namespace CacheJanitor.Tray.Charts;

/// <summary>Weekly re-downloaded bytes against the cache limit: what a larger or smaller limit would have cost.</summary>
public sealed class SizingChart : ChartBase
{
    private const double Left = 64, Right = 16, Top = 30, Bottom = 24;

    private List<SizingPoint> _points = [];
    private long _current;
    private long? _suggested;

    public void SetData(IReadOnlyList<SizingPoint> points, long currentMaxBytes, long? suggestedMaxBytes)
    {
        _points = points.OrderBy(p => p.MaxBytes).ToList();
        _current = currentMaxBytes;
        _suggested = suggestedMaxBytes;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double w = ActualWidth - Left - Right, h = ActualHeight - Top - Bottom;
        if (w < 40 || h < 30) return;

        if (_points.Count < 2 || _points.Max(p => p.WeeklyThrashBytes) <= 0)
        {
            DrawEmpty(dc, "No re-downloads recorded yet.");
            return;
        }

        long xMin = _points[0].MaxBytes, xMax = Math.Max(_points[^1].MaxBytes, xMin + 1);
        double max = _points.Max(p => p.WeeklyThrashBytes);
        double yStep = NiceByteStep(max * 1.05, 4);
        double yMax = Math.Ceiling(max * 1.05 / yStep) * yStep;
        double X(double v) => Left + (v - xMin) / (xMax - xMin) * w;
        double Y(double v) => Top + h - v / yMax * h;

        DrawLegend(dc);

        for (double v = 0; v <= yMax + 1; v += yStep)
        {
            double y = Snap(Y(v));
            dc.DrawLine(Pen(v == 0 ? ChartTheme.Baseline : ChartTheme.Grid, 1), new Point(Left, y), new Point(Left + w, y));
            var label = Text(Fmt.Size((long)v), 11, ChartTheme.InkMuted);
            dc.DrawText(label, new Point(Left - 8 - label.Width, y - label.Height / 2));
        }

        double xStep = NiceByteStep(xMax - xMin, 5);
        for (double v = Math.Ceiling(xMin / xStep) * xStep; v <= xMax + 1; v += xStep)
        {
            var label = Text(Fmt.Size((long)v), 11, ChartTheme.InkMuted);
            double x = X(v);
            if (x - label.Width / 2 >= Left - 4 && x + label.Width / 2 <= Left + w + Right)
                dc.DrawText(label, new Point(x - label.Width / 2, Top + h + 6));
        }

        for (int i = 1; i < _points.Count; i++)
        {
            var a = _points[i - 1];
            var b = _points[i];
            var dash = a.Confident && b.Confident ? DashStyles.Solid : DashStyles.Dash;
            dc.DrawLine(Pen(ChartTheme.Series1, 2, dash), new Point(X(a.MaxBytes), Y(a.WeeklyThrashBytes)), new Point(X(b.MaxBytes), Y(b.WeeklyThrashBytes)));
        }

        if (_current >= xMin && _current <= xMax)
        {
            double x = Snap(X(_current));
            dc.DrawLine(Pen(ChartTheme.InkMuted, 1, DashStyles.Dot), new Point(x, Top), new Point(x, Top + h));
            var label = Text($"now {Fmt.Size(_current)}", 11, ChartTheme.InkSecondary);
            dc.DrawText(label, new Point(Math.Min(x + 4, Left + w - label.Width), Top + 2));
        }

        if (_suggested is { } s && s >= xMin && s <= xMax)
        {
            double sx = X(s), sy = Y(Interpolate(s));
            dc.DrawEllipse(ChartTheme.Surface, null, new Point(sx, sy), 7, 7);
            dc.DrawEllipse(ChartTheme.Series2, null, new Point(sx, sy), 5, 5);
            var label = Text($"suggested {Fmt.Size(s)}", 11, ChartTheme.Ink, bold: true);
            double lx = Math.Clamp(sx - label.Width / 2, Left, Left + w - label.Width);
            double ly = sy - 12 - label.Height < Top ? sy + 12 : sy - 12 - label.Height;
            dc.DrawText(label, new Point(lx, ly));
        }

        if (Mouse is { } m && m.X >= Left && m.X <= Left + w && m.Y >= Top && m.Y <= Top + h)
        {
            long limit = (long)(xMin + (m.X - Left) / w * (xMax - xMin));
            double thrash = Interpolate(limit);
            double x = Snap(m.X);
            dc.DrawLine(Pen(ChartTheme.Baseline, 1), new Point(x, Top), new Point(x, Top + h));
            dc.DrawEllipse(ChartTheme.Surface, null, new Point(m.X, Y(thrash)), 6, 6);
            dc.DrawEllipse(ChartTheme.Series1, null, new Point(m.X, Y(thrash)), 4, 4);
            DrawTooltip(dc, new Point(m.X, Top + h / 2), $"Limit {Fmt.Size(limit)}",
                [(ChartTheme.Series1, "Re-downloaded per week", Fmt.Size((long)thrash))]);
        }
    }

    private double Interpolate(double maxBytes)
    {
        for (int i = 1; i < _points.Count; i++)
        {
            var a = _points[i - 1];
            var b = _points[i];
            if (maxBytes > b.MaxBytes) continue;
            if (b.MaxBytes == a.MaxBytes) return b.WeeklyThrashBytes;
            double t = (maxBytes - a.MaxBytes) / (b.MaxBytes - a.MaxBytes);
            return a.WeeklyThrashBytes + t * (b.WeeklyThrashBytes - a.WeeklyThrashBytes);
        }
        return _points[^1].WeeklyThrashBytes;
    }

    private void DrawLegend(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(ChartTheme.Series1, null, new Rect(Left, 5, 10, 10), 2, 2);
        var l = Text("Re-downloaded per week at each limit", 12, ChartTheme.InkSecondary);
        dc.DrawText(l, new Point(Left + 16, 2));
    }
}
