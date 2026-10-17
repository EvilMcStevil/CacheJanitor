using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CacheJanitor.Tray.Charts;

/// <summary>Shared drawing for the dashboard's charts: text, hover tracking and the tooltip box.</summary>
public abstract class ChartBase : FrameworkElement
{
    protected Point? Mouse { get; private set; }

    protected ChartBase()
    {
        ClipToBounds = false;
        Loaded += (_, _) => ChartTheme.Changed += InvalidateVisual;
        Unloaded += (_, _) => ChartTheme.Changed -= InvalidateVisual;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        Mouse = null;
        InvalidateVisual();
    }

    protected FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? ChartTheme.FaceBold : ChartTheme.Face,
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected static Pen Pen(Brush brush, double thickness, DashStyle? dash = null)
    {
        var p = new Pen(brush, thickness) { DashStyle = dash ?? DashStyles.Solid, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    /// <summary>Snaps a coordinate to the pixel grid so 1px hairlines stay crisp.</summary>
    protected static double Snap(double v) => Math.Round(v) + 0.5;

    protected void DrawEmpty(DrawingContext dc, string message)
    {
        // A transparent fill so the element still receives mouse events and keeps its layout size.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var t = Text(message, 12, ChartTheme.InkMuted);
        dc.DrawText(t, new Point((ActualWidth - t.Width) / 2, (ActualHeight - t.Height) / 2));
    }

    /// <summary>A tooltip near <paramref name="anchor"/>, kept inside the chart. Rows are (swatch, label, value).</summary>
    protected void DrawTooltip(DrawingContext dc, Point anchor, string title, IReadOnlyList<(Brush? Swatch, string Label, string Value)> rows)
    {
        const double pad = 10, swatch = 8, lineGap = 4;
        var titleText = Text(title, 12, ChartTheme.Ink, bold: true);
        var labels = rows.Select(r => Text(r.Label, 12, ChartTheme.InkSecondary)).ToList();
        var values = rows.Select(r => Text(r.Value, 12, ChartTheme.Ink, bold: true)).ToList();
        double labelW = labels.Count == 0 ? 0 : labels.Max(l => l.Width);
        double valueW = values.Count == 0 ? 0 : values.Max(v => v.Width);
        bool anySwatch = rows.Any(r => r.Swatch is not null);
        double swatchW = anySwatch ? swatch + 6 : 0;
        double width = Math.Max(titleText.Width, swatchW + labelW + 16 + valueW) + 2 * pad;
        double rowH = Math.Max(titleText.Height, 16);
        double height = pad * 2 + titleText.Height + rows.Count * (rowH + lineGap);

        double x = anchor.X + 14;
        if (x + width > ActualWidth) x = anchor.X - 14 - width;
        x = Math.Max(0, x);
        double y = Math.Clamp(anchor.Y - height / 2, 0, Math.Max(0, ActualHeight - height));

        dc.DrawRoundedRectangle(ChartTheme.TooltipBackground, Pen(ChartTheme.TooltipBorder, 1), new Rect(x, y, width, height), 6, 6);
        dc.DrawText(titleText, new Point(x + pad, y + pad));
        double ry = y + pad + titleText.Height + lineGap;
        for (int i = 0; i < rows.Count; i++)
        {
            double cx = x + pad;
            if (rows[i].Swatch is { } b)
                dc.DrawRoundedRectangle(b, null, new Rect(cx, ry + (rowH - swatch) / 2, swatch, swatch), 2, 2);
            cx += swatchW;
            dc.DrawText(labels[i], new Point(cx, ry));
            dc.DrawText(values[i], new Point(x + width - pad - values[i].Width, ry));
            ry += rowH + lineGap;
        }
    }

    /// <summary>Round "nice" step for an axis spanning 0..max with about <paramref name="ticks"/> intervals.</summary>
    protected static double NiceStep(double max, int ticks)
    {
        if (max <= 0) return 1;
        double raw = max / ticks;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
        return nice * mag;
    }

    /// <summary>Like <see cref="NiceStep"/> but in binary size units, so byte axes read 0, 25 GB, 50 GB…</summary>
    protected static double NiceByteStep(double max, int ticks)
    {
        if (max <= 0) return 1;
        double unit = 1;
        while (max / unit >= 1024 && unit < Math.Pow(1024, 5)) unit *= 1024;
        return NiceStep(max / unit, ticks) * unit;
    }
}
