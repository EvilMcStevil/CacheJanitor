using System.IO;
using System.ServiceProcess;
using System.Windows.Threading;

namespace CacheJanitor.Tray;

public enum ServiceState { NotInstalled, Stopped, Starting, Running, Stopping }

/// <summary>
/// Polls the service on the UI thread's dispatcher: quickly while the dashboard is open, slowly when only the
/// tray icon needs keeping current.
/// </summary>
public sealed class StatusPoller
{
    public static readonly TimeSpan Fast = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(15);

    private readonly DispatcherTimer _timer = new() { Interval = Slow };
    private bool _busy;

    public event Action? Updated;

    public ServiceStatus? Status { get; private set; }
    public ServiceState Service { get; private set; } = ServiceState.NotInstalled;

    /// <summary>Why <see cref="Status"/> is null, when the service is installed and running but not answering.</summary>
    public string? Error { get; private set; }

    public bool FastPolling
    {
        get => _timer.Interval == Fast;
        set => _timer.Interval = value ? Fast : Slow;
    }

    public void Start()
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Service = await Task.Run(QueryService);
            if (Service is ServiceState.Stopped or ServiceState.Stopping)
            {
                Status = null;
                Error = null;
            }
            else
            {
                try
                {
                    Status = await JanitorClient.GetStatusAsync();
                    Error = null;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.TimeoutException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    Status = null;
                    Error = Service == ServiceState.Running ? "The service is running but not answering." : null;
                }
            }
        }
        finally
        {
            _busy = false;
        }
        Updated?.Invoke();
    }

    /// <summary>Asks the service for a pass now, then refreshes shortly after so the result shows.</summary>
    public async Task<string?> TrimNowAsync(string? path)
    {
        try
        {
            var r = await JanitorClient.SendAsync(new PipeRequest { Command = "trim", Path = path }, TimeSpan.FromSeconds(5));
            await Task.Delay(750);
            await RefreshAsync();
            return r.Ok ? null : r.Error;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.TimeoutException or UnauthorizedAccessException)
        {
            return "The service is not reachable.";
        }
    }

    private static ServiceState QueryService()
    {
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.Paused
                    or ServiceControllerStatus.PausePending => ServiceState.Running,
                ServiceControllerStatus.StartPending => ServiceState.Starting,
                ServiceControllerStatus.StopPending => ServiceState.Stopping,
                _ => ServiceState.Stopped,
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotInstalled;
        }
    }
}
