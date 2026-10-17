using System.Globalization;
using CacheJanitor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage:
//   CacheJanitor.exe                       run as a Windows service (or in the console when started by hand)
//   CacheJanitor.exe once [--dry-run]      one pass over every cache in config.json, then exit
//   CacheJanitor.exe trim <path> <maxSize> [--unit File|TopLevelEntry] [--trim-to 90] [--min-age 10] [--dry-run]
//                                          one pass over a single folder without any config file

var mode = args.FirstOrDefault()?.ToLowerInvariant();

if (mode is "-h" or "--help" or "/?" or "help")
{
    Console.WriteLine($"""
        CacheJanitor - keeps cache folders under a size limit by deleting least recently used entries.

          CacheJanitor.exe                     run as a service (or in the foreground if started by hand)
          CacheJanitor.exe once [--dry-run]    trim every cache in config.json once and exit
          CacheJanitor.exe trim <path> <maxSize> [--unit File|TopLevelEntry] [--trim-to 90] [--min-age 10] [--dry-run]
                                               trim one folder once, no config needed

        Configuration: {AppPaths.ConfigPath}
        """);
    return 0;
}

if (mode is "once" or "trim")
    return await RunOnceAsync(mode, args.Skip(1).ToArray());

// ---- Service / foreground mode ----
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Services start in C:\Windows\System32, so read appsettings.json from next to the exe.
    ContentRootPath = AppContext.BaseDirectory,
});

using (var bootLoggers = LoggerFactory.Create(b => b.AddConsole()))
    DataDirectory.EnsureSecured(bootLoggers.CreateLogger("CacheJanitor"));

builder.Configuration.AddJsonFile(AppPaths.ConfigPath, optional: true, reloadOnChange: true);
builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
builder.Services.Configure<JanitorOptions>(builder.Configuration.GetSection(ConfigFile.SectionName));
builder.Services.AddSingleton<StatsStore>();
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());
builder.Services.AddHostedService<PipeServer>();
// When running as a service, AddWindowsService also logs to the Windows Event Log (Application, source "CacheJanitor").

await builder.Build().RunAsync();
return Environment.ExitCode;

static async Task<int> RunOnceAsync(string mode, string[] rest)
{
    bool dryRun = rest.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    using var loggers = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
        .SetMinimumLevel(rest.Contains("--verbose", StringComparer.OrdinalIgnoreCase) ? LogLevel.Debug : LogLevel.Information));
    var log = loggers.CreateLogger("CacheJanitor");

    List<CacheFolderOptions> caches;
    try
    {
        caches = mode == "trim" ? [ParseTrimArgs(rest)] : ConfigFile.Load(AppPaths.ConfigPath).Caches;
    }
    catch (Exception ex) when (ex is FormatException or ArgumentException or System.Text.Json.JsonException)
    {
        log.LogError("{Message}", ex.Message);
        return 2;
    }

    if (caches.Count == 0)
    {
        log.LogError("No caches configured in {Config}.", AppPaths.ConfigPath);
        return 2;
    }

    LastAccessCheck.WarnIfDisabled(log);

    int exit = 0;
    foreach (var c in caches)
    {
        if (PathGuard.Check(c.Path) is { } problem)
        {
            log.LogError("{Path}: {Problem}", c.Path, problem);
            exit = 2;
            continue;
        }
        try
        {
            var r = await new CacheTrimmer(c, log, dryRun).TrimAsync();
            Console.WriteLine($"{c.Path}: {SizeParser.Format(r.BytesBefore)} -> {SizeParser.Format(r.BytesAfter)} " +
                              $"(limit {SizeParser.Format(c.MaxBytes)}, evicted {r.Evicted}, in use {r.SkippedInUse})");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Trim of {Path} failed.", c.Path);
            exit = 1;
        }
    }
    return exit;
}

static CacheFolderOptions ParseTrimArgs(string[] a)
{
    var positional = new List<string>();
    var c = new CacheFolderOptions();
    for (int i = 0; i < a.Length; i++)
    {
        string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{a[i]} needs a value.");
        switch (a[i].ToLowerInvariant())
        {
            case "--unit": c.Unit = Enum.Parse<EvictionUnit>(Next(), ignoreCase: true); break;
            case "--trim-to": c.TrimToPercent = double.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--min-age": c.MinAgeMinutes = double.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--exclude": c.Exclude.Add(Next()); break;
            case "--dry-run": case "--verbose": break;
            default: positional.Add(a[i]); break;
        }
    }
    if (positional.Count != 2)
        throw new ArgumentException("Usage: CacheJanitor.exe trim <path> <maxSize> [options]");
    c.Path = positional[0];
    c.MaxSize = positional[1];
    _ = c.MaxBytes; // validate
    return c;
}
