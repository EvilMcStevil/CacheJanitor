using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace CacheJanitor.Tray;

/// <summary>
/// Actions that need administrator rights. The tray runs unelevated, so it starts a second copy of itself
/// through UAC with one of these argument sets; that copy does the one job and exits with its result code.
/// </summary>
public static class Elevated
{
    private const string SaveConfigArg = "--save-config";
    private const string ServiceArg = "--service";

    /// <summary>Exit codes the helper returns.</summary>
    public const int Success = 0, Failed = 1, Invalid = 2, Cancelled = -1;

    /// <summary>Runs a helper mode if <paramref name="args"/> asks for one; returns its exit code, or null for the normal UI.</summary>
    public static int? TryRun(string[] args)
    {
        if (args.Length == 2 && args[0] == SaveConfigArg)
            return SaveConfigFrom(args[1]);
        if (args.Length == 2 && args[0] == ServiceArg)
            return ControlService(args[1]);
        return null;
    }

    /// <summary>Writes config.json, asking for elevation first unless this process already has it.</summary>
    public static async Task<(int Code, string? Error)> SaveConfigAsync(JanitorOptions options)
    {
        if (ConfigFile.Validate(options) is { Count: > 0 } errors)
            return (Invalid, string.Join(Environment.NewLine, errors));

        if (DataDirectory.IsElevated())
        {
            try
            {
                Save(options);
                return (Success, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (Failed, ex.Message);
            }
        }

        // Normally only administrators can write here, but try first: the folder may grant this user access.
        // Never create the folder unelevated, or it would be owned by this user.
        if (Directory.Exists(AppPaths.DataDirectory))
        {
            try
            {
                ConfigFile.Save(AppPaths.ConfigPath, options);
                return (Success, null);
            }
            catch (UnauthorizedAccessException) { }
        }

        var temp = Path.Combine(Path.GetTempPath(), $"CacheJanitor-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(temp, ConfigFile.Serialize(options));
        try
        {
            var code = await RunElevatedAsync(SaveConfigArg, temp);
            return (code, code == Failed ? $"Could not write {AppPaths.ConfigPath}." : null);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static Task<int> ControlServiceAsync(string action) =>
        DataDirectory.IsElevated() ? Task.Run(() => ControlService(action)) : RunElevatedAsync(ServiceArg, action);

    private static async Task<int> RunElevatedAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the user said no to UAC
        {
            return Cancelled;
        }
    }

    private static int SaveConfigFrom(string path)
    {
        try
        {
            var options = ConfigFile.Load(path);
            if (ConfigFile.Validate(options).Count > 0) return Invalid;
            Save(options);
            return Success;
        }
        catch (Exception)
        {
            return Failed;
        }
    }

    private static void Save(JanitorOptions options)
    {
        DataDirectory.EnsureSecured();
        ConfigFile.Save(AppPaths.ConfigPath, options);
    }

    private static int ControlService(string action)
    {
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            var timeout = TimeSpan.FromSeconds(30);
            if (action is "stop" or "restart" && sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }
            if (action is "start" or "restart")
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            }
            return Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or Win32Exception)
        {
            return Failed;
        }
    }
}
