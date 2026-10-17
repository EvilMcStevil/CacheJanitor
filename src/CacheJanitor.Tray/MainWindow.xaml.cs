using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CacheJanitor.Tray.Charts;
using Microsoft.Win32;

namespace CacheJanitor.Tray;

public partial class MainWindow : Window
{
    private readonly StatusPoller _poller = App.Current.Poller;
    private TimeSpan _range = TimeSpan.FromHours(24);
    private bool _syncingRange;

    public MainModel Model { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = Model;
        LoadConfig();

        FilesInfo.Text = $"Settings: {AppPaths.ConfigPath}\nStatistics: {AppPaths.StatsPath}\n" +
                         "Logs: Windows Event Viewer → Windows Logs → Application, source \"CacheJanitor\".";

        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainModel.Selected)) RenderCharts();
        };
        Model.Caches.CollectionChanged += (_, _) => EmptyListHint.Visibility = Model.Caches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyListHint.Visibility = Model.Caches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _poller.Updated += OnStatus;
        _poller.FastPolling = true;
        IsVisibleChanged += (_, _) => _poller.FastPolling = IsVisible;
        Closed += (_, _) =>
        {
            _poller.Updated -= OnStatus;
            _poller.FastPolling = false;
        };
        OnStatus();
    }

    private void LoadConfig()
    {
        try
        {
            Model.Load(ConfigFile.Load(AppPaths.ConfigPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Model.Load(new JanitorOptions());
            MessageBox.Show(this, $"Could not read {AppPaths.ConfigPath}:\n{ex.Message}", "CacheJanitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnStatus()
    {
        var status = _poller.Status;
        var byPath = status?.Caches.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase) ?? new();
        foreach (var item in Model.Caches)
        {
            CacheStatus? s = null;
            try
            {
                if (PathGuard.Check(item.Path) is null)
                    byPath.TryGetValue(PathGuard.Normalize(item.Path), out s);
            }
            catch (ArgumentException) { }
            item.NoStatusReason = status is null ? ServiceSummary().Short : "Not being managed: save to apply";
            item.Status = s;
        }

        var (shortText, level) = ServiceSummary();
        ServiceText.Text = status is null ? shortText : $"{shortText} · version {status.Version} · up {Fmt.Duration(DateTime.UtcNow - status.StartedUtc)}";
        ServiceDot.Fill = ChartTheme.Status(level);

        var svc = _poller.Service;
        StartButton.Visibility = svc is ServiceState.Stopped ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = RestartButton.Visibility = svc is ServiceState.Running ? Visibility.Visible : Visibility.Collapsed;
        TrimAllButton.IsEnabled = status is not null;

        var notices = new List<Notice>();
        if (svc == ServiceState.NotInstalled && status is null)
            notices.Add(new Notice("The CacheJanitor service is not installed", "Install CacheJanitor.msi to have folders managed. Settings can still be edited here.", Level.Warning));
        else if (svc == ServiceState.Stopped)
            notices.Add(new Notice("The service is stopped", "Nothing is being trimmed. Start it to resume.", Level.Critical));
        else if (_poller.Error is { } err)
            notices.Add(new Notice("No status from the service", err, Level.Warning));
        if (status?.LastAccessEnabled == false)
            notices.Add(new Notice("Windows is not recording when files are read",
                "NTFS last-access updates are off, so files are evicted by when they were downloaded rather than when they were last used. " +
                "To fix, run \"fsutil behavior set disablelastaccess 0\" as administrator and restart.", Level.Warning));
        if (status?.ConfigErrors is { Count: > 0 } errors)
            notices.Add(new Notice("Some settings were rejected by the service", string.Join("\n", errors), Level.Critical));
        Notices.ItemsSource = notices;

        RenderCharts();
    }

    private (string Short, Level Level) ServiceSummary() => _poller.Service switch
    {
        ServiceState.Running when _poller.Status is not null => ("Service running", Level.Good),
        ServiceState.Running => ("Service not answering", Level.Warning),
        ServiceState.Starting => ("Service starting", Level.Warning),
        ServiceState.Stopping => ("Service stopping", Level.Warning),
        ServiceState.Stopped => ("Service stopped", Level.Critical),
        _ when _poller.Status is not null => ("Running in a console", Level.Good),
        _ => ("Service not installed", Level.Neutral),
    };

    private void RenderCharts()
    {
        var item = Model.Selected;
        var s = item?.Status;
        SizeChart.SetData(s?.History ?? [], item?.LimitBytes ?? 0, item?.TargetBytes ?? 0, _range);
        ChurnChart.SetData(s?.History ?? [], _range);
        AgeChart.SetData(s?.AgeBytes ?? [], s?.AgeEntries ?? [], s?.Unit == EvictionUnit.TopLevelEntry);
    }

    private void SetRange(TimeSpan range)
    {
        if (_syncingRange || !IsInitialized) return; // radio buttons fire Checked while the XAML loads
        _range = range;
        _syncingRange = true;
        bool week = range > TimeSpan.FromDays(1);
        SizeRange7.IsChecked = ChurnRange7.IsChecked = week;
        SizeRange24.IsChecked = ChurnRange24.IsChecked = !week;
        _syncingRange = false;
        RenderCharts();
    }

    private void Range24_Checked(object sender, RoutedEventArgs e) => SetRange(TimeSpan.FromHours(24));
    private void Range7_Checked(object sender, RoutedEventArgs e) => SetRange(TimeSpan.FromDays(7));

    // ---- service ----

    private async void TrimAll_Click(object sender, RoutedEventArgs e)
    {
        TrimAllButton.IsEnabled = false;
        try
        {
            if (await _poller.TrimNowAsync(null) is { } error)
                MessageBox.Show(this, error, "CacheJanitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TrimAllButton.IsEnabled = _poller.Status is not null;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => ControlService("start");
    private void Stop_Click(object sender, RoutedEventArgs e) => ControlService("stop");
    private void Restart_Click(object sender, RoutedEventArgs e) => ControlService("restart");

    private async void ControlService(string action)
    {
        var code = await Elevated.ControlServiceAsync(action);
        if (code == Elevated.Failed)
            MessageBox.Show(this, $"Could not {action} the service. Event Viewer may say why.", "CacheJanitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        await _poller.RefreshAsync();
    }

    // ---- folders ----

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a cache folder" };
        if (dialog.ShowDialog(this) != true) return;
        if (PathGuard.Check(dialog.FolderName) is { } problem)
        {
            MessageBox.Show(this, $"{dialog.FolderName} cannot be managed: {problem}", "CacheJanitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var item = new CacheItem(new CacheFolderOptions { Path = dialog.FolderName, MaxSize = SuggestLimit(dialog.FolderName) });
        Model.Add(item);
        Model.Selected = item;
        Tabs.SelectedItem = SettingsTab;
    }

    private void SeeSizing_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = SizingTab;

    /// <summary>A starting limit: a quarter of the drive's total size, rounded to a whole number of gigabytes.</summary>
    private static string SuggestLimit(string folder)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            long gb = Math.Max(1, drive.TotalSize / 4 / (1L << 30));
            return $"{gb}GB";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "50GB";
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Selected is not { } item) return;
        var answer = MessageBox.Show(this,
            $"Stop managing {item.Title}?\n\nNothing in the folder is deleted. It just stops being trimmed once you save.",
            "CacheJanitor", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK)
            Model.Remove(item);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Selected is not { } item) return;
        var dialog = new OpenFolderDialog { Title = "Choose a cache folder" };
        if (Directory.Exists(item.Path)) dialog.InitialDirectory = item.Path;
        if (dialog.ShowDialog(this) == true)
            item.Path = dialog.FolderName;
    }

    private void MaxSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = ((TextBox)sender).Text;
        SizeEcho.Text = SizeParser.TryParse(text, out var bytes) ? $"= {Fmt.Size(bytes)}" : string.IsNullOrWhiteSpace(text) ? "" : "not a size";
    }

    // ---- saving ----

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var options = Model.ToOptions();
        SaveButton.IsEnabled = false;
        try
        {
            var (code, error) = await Elevated.SaveConfigAsync(options);
            switch (code)
            {
                case Elevated.Success:
                    Model.MarkSaved();
                    await _poller.RefreshAsync();
                    break;
                case Elevated.Cancelled:
                    break;
                case Elevated.Invalid:
                    MessageBox.Show(this, "Please fix these first:\n\n" + error, "CacheJanitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                default:
                    MessageBox.Show(this, error ?? "Saving failed.", "CacheJanitor", MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        LoadConfig();
        OnStatus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Model.IsDirty)
        {
            var answer = MessageBox.Show(this, "Save your changes before closing?", "CacheJanitor",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                if (answer == MessageBoxResult.Yes) Save_Click(this, new RoutedEventArgs());
                return;
            }
        }
        base.OnClosing(e);
    }

    // ---- files ----

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(AppPaths.DataDirectory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDirectory}\"") { UseShellExecute = true });
    }

    private void OpenEventViewer_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true });
}
