using System.Globalization;

namespace CacheJanitor.Tray;

/// <summary>Human-readable sizes, counts and durations for the dashboard.</summary>
public static class Fmt
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        int i = 0;
        while (Math.Abs(v) >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        var format = i == 0 || Math.Abs(v) >= 100 ? "0" : Math.Abs(v) >= 10 ? "0.#" : "0.##";
        return v.ToString(format, Culture) + " " + units[i];
    }

    public static string Count(long n) => n.ToString("N0", Culture);

    public static string Percent(double fraction) => (fraction * 100).ToString(fraction < 0.1 ? "0.#" : "0", Culture) + "%";

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:0} s";
        if (t.TotalMinutes < 60) return $"{t.TotalMinutes:0} min";
        if (t.TotalHours < 24) return t.Minutes == 0 || t.TotalHours >= 10 ? $"{t.TotalHours:0} h" : $"{t.Hours} h {t.Minutes} min";
        if (t.TotalDays < 14) return t.Hours == 0 ? $"{t.Days} d" : $"{t.Days} d {t.Hours} h";
        return $"{t.TotalDays:0} days";
    }

    public static string Ago(DateTime? utc) =>
        utc is null ? "never" : DateTime.UtcNow - utc.Value < TimeSpan.FromSeconds(5) ? "just now" : Duration(DateTime.UtcNow - utc.Value) + " ago";

    public static string Until(DateTime? utc) =>
        utc is null ? "—" : utc.Value <= DateTime.UtcNow ? "due now" : "in " + Duration(utc.Value - DateTime.UtcNow);

    public static string LocalTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("t", Culture)
            : local.ToString("ddd d MMM, t", Culture);
    }
}
