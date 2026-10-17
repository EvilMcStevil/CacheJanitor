using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CacheJanitor.Tray.Charts;

public enum Level { Neutral, Good, Warning, Critical }

/// <summary>
/// Chart ink and series colours, with separately chosen light and dark steps. Series 1 (blue) is size and
/// ingress, series 2 (orange) is evictions; status colours are used only for fill level and are always shown
/// next to a text label.
/// </summary>
public static class ChartTheme
{
    public static event Action? Changed;

    public static bool IsDark { get; private set; } = ReadIsDark();

    static ChartTheme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var dark = ReadIsDark();
            if (dark == IsDark) return;
            IsDark = dark;
            Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        };
    }

    public static Brush Ink => B(IsDark ? "#F0EFEC" : "#0B0B0B");
    public static Brush InkSecondary => B(IsDark ? "#C3C2B7" : "#52514E");
    public static Brush InkMuted => B("#898781");
    public static Brush Grid => B(IsDark ? "#14FFFFFF" : "#14000000");
    public static Brush Baseline => B(IsDark ? "#4A4A47" : "#C3C2B7");
    public static Brush Series1 => B(IsDark ? "#3987E5" : "#2A78D6");
    public static Brush Series1Area => B(IsDark ? "#333987E5" : "#262A78D6");
    public static Brush Series2 => B(IsDark ? "#D95926" : "#EB6834");
    public static Brush TooltipBackground => B(IsDark ? "#F22C2C2A" : "#F7FFFFFF");
    public static Brush TooltipBorder => B(IsDark ? "#1AFFFFFF" : "#1A0B0B0B");

    /// <summary>The chart's own background, used for the ring that separates a hover marker from the line.</summary>
    public static Brush Surface => B(IsDark ? "#2B2B2B" : "#FBFBFB");

    public static Brush Status(Level level) => level switch
    {
        Level.Good => B("#0CA30C"),
        Level.Warning => B("#FAB219"),
        Level.Critical => B("#D03B3B"),
        _ => InkMuted,
    };

    public static Level LevelOf(double fraction, bool error) =>
        error || fraction > 1 ? Level.Critical : fraction >= 0.9 ? Level.Warning : Level.Good;

    public static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface FaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    private static SolidColorBrush B(string hex)
    {
        if (!Cache.TryGetValue(hex, out var b))
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            Cache[hex] = b;
        }
        return b;
    }

    private static bool ReadIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
