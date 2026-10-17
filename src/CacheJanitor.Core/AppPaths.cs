namespace CacheJanitor;

/// <summary>Well-known names and locations shared by the service, the tray app and the installer.</summary>
public static class AppPaths
{
    public const string ServiceName = "CacheJanitor";

    /// <summary>Named pipe the service answers status requests on.</summary>
    public const string PipeName = "CacheJanitor";

    /// <summary>
    /// %ProgramData%\CacheJanitor, writable only by SYSTEM and Administrators. The CACHEJANITOR_DATA environment
    /// variable points it elsewhere, for running a test instance in the console.
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("CACHEJANITOR_DATA") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CacheJanitor");

    /// <summary>The cache list and schedule. The service reloads it whenever it changes.</summary>
    public static string ConfigPath { get; } = Path.Combine(DataDirectory, "config.json");

    /// <summary>Lifetime counters and size history, kept across service restarts.</summary>
    public static string StatsPath { get; } = Path.Combine(DataDirectory, "stats.json");
}
