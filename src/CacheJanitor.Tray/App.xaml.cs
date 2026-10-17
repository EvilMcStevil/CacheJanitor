using System.Windows;

namespace CacheJanitor.Tray;

/// <summary>
/// The tray app. Lives in the notification area and opens the dashboard on demand. Started with
/// <c>--background</c> (as it is at sign-in) it shows only the tray icon. The elevated helper modes in
/// <see cref="Elevated"/> run instead of the UI when their arguments are present.
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showRequest;
    private TrayIcon? _tray;
    private MainWindow? _window;

    public StatusPoller Poller { get; } = new();

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Elevated.TryRun(e.Args) is { } exitCode)
        {
            Shutdown(exitCode);
            return;
        }

        _single = new Mutex(initiallyOwned: true, @"Local\CacheJanitorTray", out bool first);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CacheJanitorTray.Show");
        if (!first)
        {
            // Already running in this session: bring its window up instead.
            _showRequest.Set();
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => Dispatcher.BeginInvoke(ShowDashboard), null, Timeout.Infinite, false);

        _tray = new TrayIcon(this);
        Poller.Start();
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            ShowDashboard();
    }

    public void ShowDashboard()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>The dashboard's working copy of the settings, if it is open and has unsaved edits.</summary>
    public bool HasUnsavedChanges => _window?.Model.IsDirty == true;

    public void Quit()
    {
        if (_window is not null)
        {
            _window.Close();
            if (_window is not null) return; // the user kept the window open to save
        }
        _tray?.Dispose();
        Poller.Stop();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
