using System.Windows;
using System.Windows.Media;

namespace CacheJanitor.Tray.Charts;

/// <summary>A slim fill-level meter: used / limit, with a tick at the trim target.</summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(UsageBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetFractionProperty = DependencyProperty.Register(
        nameof(TargetFraction), typeof(double), typeof(UsageBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(Level), typeof(UsageBar), new FrameworkPropertyMetadata(Level.Neutral, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public double TargetFraction { get => (double)GetValue(TargetFractionProperty); set => SetValue(TargetFractionProperty, value); }
    public Level Level { get => (Level)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    public UsageBar()
    {
        Height = 6;
        Loaded += (_, _) => ChartTheme.Changed += InvalidateVisual;
        Unloaded += (_, _) => ChartTheme.Changed -= InvalidateVisual;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(ChartTheme.Grid, null, new Rect(0, 0, w, h), h / 2, h / 2);
        double fill = Math.Clamp(Fraction, 0, 1) * w;
        if (fill > 0)
            dc.DrawRoundedRectangle(ChartTheme.Status(Level), null, new Rect(0, 0, Math.Max(fill, h), h), h / 2, h / 2);
        if (TargetFraction is > 0 and < 1)
        {
            double x = Math.Round(TargetFraction * w) + 0.5;
            dc.DrawLine(new Pen(ChartTheme.InkSecondary, 1), new Point(x, -2), new Point(x, h + 2));
        }
    }
}
