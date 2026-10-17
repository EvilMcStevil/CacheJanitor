using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CacheJanitor.Tray.Charts;

namespace CacheJanitor.Tray;

public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Level level ? ChartTheme.Status(level) : ChartTheme.InkMuted;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible for warning and critical levels, collapsed otherwise.</summary>
public sealed class LevelToVisibility : IValueConverter
{
    public static readonly LevelToVisibility Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Level.Warning or Level.Critical ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class UnitToText : IValueConverter
{
    public static readonly UnitToText Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        EvictionUnit.File => "Individual files",
        EvictionUnit.TopLevelEntry => "Whole top-level entries",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A banner across the top of the dashboard.</summary>
public sealed record Notice(string Title, string Detail, Level Level);

/// <summary>The UAC shield, shown on buttons that need elevation; hidden when the app is already elevated.</summary>
public static class Shield
{
    public static ImageSource? Icon { get; } = Load();

    public static Visibility Visibility { get; } = DataDirectory.IsElevated() ? Visibility.Collapsed : Visibility.Visible;

    private static ImageSource? Load()
    {
        try
        {
            var icon = System.Drawing.SystemIcons.GetStockIcon(System.Drawing.StockIconId.Shield, System.Drawing.StockIconOptions.SmallIcon);
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
