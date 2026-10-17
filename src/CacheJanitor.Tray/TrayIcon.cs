using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CacheJanitor.Tray;

/// <summary>
/// The notification-area icon. It is drawn live as a small gauge of the fullest cache: white while there is
/// room, amber from 90% of a limit, red when a cache is over its limit or failing, and grey when the service
/// is not running. Its tooltip lists each folder's fill level.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _trim, _start, _stop;
    private readonly Dictionary<string, CacheState> _lastStates = new(StringComparer.OrdinalIgnoreCase);
    private nint _hIcon;
    private string _iconKey = "";

    public TrayIcon(App app)
    {
        _app = app;
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open CacheJanitor", null, (_, _) => _app.ShowDashboard()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        _trim = new ToolStripMenuItem("Trim all now", null, async (_, _) => await _app.Poller.TrimNowAsync(null));
        _start = new ToolStripMenuItem("Start service", null, async (_, _) => { await Elevated.ControlServiceAsync("start"); await _app.Poller.RefreshAsync(); });
        _stop = new ToolStripMenuItem("Stop service", null, async (_, _) => { await Elevated.ControlServiceAsync("stop"); await _app.Poller.RefreshAsync(); });
        if (DataDirectory.IsElevated() is false)
        {
            var shield = SystemIcons.GetStockIcon(StockIconId.Shield, StockIconOptions.SmallIcon).ToBitmap();
            _start.Image = _stop.Image = shield;
        }
        menu.Items.AddRange([open, new ToolStripSeparator(), _trim, _start, _stop, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => _app.Quit())]);

        _icon = new NotifyIcon { ContextMenuStrip = menu, Text = "CacheJanitor", Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _app.ShowDashboard(); };
        _icon.BalloonTipClicked += (_, _) => _app.ShowDashboard();

        _app.Poller.Updated += Update;
        Update();
    }

    private void Update()
    {
        var poller = _app.Poller;
        var status = poller.Status;
        _trim.Enabled = status is not null;
        _start.Visible = poller.Service == ServiceState.Stopped;
        _stop.Visible = poller.Service == ServiceState.Running;

        double worst = 0;
        bool failing = false;
        var lines = new List<string>();
        var folders = new List<(string Name, string Usage, string? Tag)>();
        if (status is null)
        {
            lines.Add(poller.Service switch
            {
                ServiceState.NotInstalled => "Service not installed",
                ServiceState.Stopped => "Service stopped",
                ServiceState.Starting => "Service starting…",
                _ => "Service not answering",
            });
        }
        else if (status.Caches.Count == 0)
        {
            lines.Add("No folders configured");
        }
        else
        {
            foreach (var c in status.Caches)
            {
                double f = c.MaxBytes > 0 ? c.CurrentBytes / (double)c.MaxBytes : 0;
                worst = Math.Max(worst, f);
                failing |= c.State is CacheState.Error or CacheState.OverLimit;
                var name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileName(c.Path) : c.Name;
                folders.Add((name, c.State == CacheState.Error ? "error" : $"{Fmt.Percent(f)} of {Fmt.Size(c.MaxBytes)}", c.State == CacheState.Error ? null : AdviceTag(c.Sizing)));
                Notify(c, name);
            }
        }

        const string Header = "CacheJanitor";
        _icon.Text = folders.Count > 0
            ? Header + "\n" + TooltipText(folders, MaxTooltipLength - Header.Length - 1)
            : string.Join("\n", lines.Prepend(Header));

        Color fill = failing || worst > 1 ? Color.FromArgb(0xD0, 0x3B, 0x3B)
            : worst >= 0.9 ? Color.FromArgb(0xFA, 0xB2, 0x19)
            : Color.White;
        SetIcon(status is null ? null : Math.Clamp(worst, 0, 1), fill);
    }

    /// <summary>NotifyIcon.Text is limited to 127 characters.</summary>
    private const int MaxTooltipLength = 127;

    /// <summary>The short sizing hint shown beside a folder in the tooltip, e.g. "↑ 25 GB"; null when there is no suggestion.</summary>
    internal static string? AdviceTag(SizingAdvice? s) => s switch
    {
        { Verdict: SizingVerdict.TooSmall, SuggestedMaxBytes: { } b } => $"↑ {Fmt.Size(b)}",
        { Verdict: SizingVerdict.CouldBeSmaller, SuggestedMaxBytes: { } b } => $"↓ {Fmt.Size(b)}",
        _ => null,
    };

    /// <summary>
    /// Joins one line per folder, "name: usage" with an optional " · tag". When the result is too long, every
    /// name is shortened to a common length (no fewer than 6 characters plus an ellipsis); if that is still too
    /// long the text is cut.
    /// </summary>
    internal static string TooltipText(IReadOnlyList<(string Name, string Usage, string? Tag)> lines, int max = MaxTooltipLength)
    {
        string Build(int nameLength) => string.Join("\n", lines.Select(l =>
        {
            var name = l.Name.Length > nameLength ? l.Name[..nameLength] + "…" : l.Name;
            return l.Tag is null ? $"{name}: {l.Usage}" : $"{name}: {l.Usage} · {l.Tag}";
        }));

        var text = Build(int.MaxValue);
        for (int n = lines.Count == 0 ? 0 : lines.Max(l => l.Name.Length) - 1; text.Length > max && n >= 6; n--)
            text = Build(n);
        return text.Length <= max ? text : text[..(max - 3)] + "…";
    }

    /// <summary>Shows a notification when a folder newly goes over its limit or starts failing.</summary>
    private void Notify(CacheStatus c, string name)
    {
        bool known = _lastStates.TryGetValue(c.Path, out var previous);
        _lastStates[c.Path] = c.State;
        if (!known || previous == c.State) return;
        if (c.State == CacheState.OverLimit)
            _icon.ShowBalloonTip(10_000, $"{name} is over its limit",
                $"{Fmt.Size(c.CurrentBytes)} of {Fmt.Size(c.MaxBytes)}, and everything left is in use, excluded or too new to evict.", ToolTipIcon.Warning);
        else if (c.State == CacheState.Error)
            _icon.ShowBalloonTip(10_000, $"{name} cannot be trimmed", c.Error ?? "The last pass failed.", ToolTipIcon.Error);
    }

    /// <summary>Draws the gauge icon; <paramref name="fraction"/> null means "service unavailable".</summary>
    private void SetIcon(double? fraction, Color fill)
    {
        int size = SystemInformation.SmallIconSize.Width;
        var key = $"{size}:{fraction:0.00}:{fill.ToArgb()}";
        if (key == _iconKey) return;
        _iconKey = key;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size;
            var tile = new RectangleF(0.5f, 0.5f, s - 1, s - 1);
            using var tileBrush = new SolidBrush(fraction is null ? Color.FromArgb(0x6E, 0x6E, 0x6E) : Color.FromArgb(0x10, 0x6E, 0x80));
            using var tilePath = Rounded(tile, s * 0.22f);
            g.FillPath(tileBrush, tilePath);

            // Gauge well, then the fill rising from the bottom.
            float pad = s * 0.2f;
            var well = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
            using var wellBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
            using var wellPath = Rounded(well, s * 0.1f);
            g.FillPath(wellBrush, wellPath);
            if (fraction is { } f && f > 0)
            {
                float h = Math.Max(s * 0.08f, well.Height * (float)f);
                var level = new RectangleF(well.X, well.Bottom - h, well.Width, h);
                using var fillBrush = new SolidBrush(fill);
                g.SetClip(wellPath);
                g.FillRectangle(fillBrush, level);
                g.ResetClip();
            }
        }

        var hIcon = bmp.GetHicon();
        var old = _icon.Icon;
        _icon.Icon = Icon.FromHandle(hIcon);
        old?.Dispose();
        if (_hIcon != 0) DestroyIcon(_hIcon);
        _hIcon = hIcon;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _app.Poller.Updated -= Update;
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != 0) DestroyIcon(_hIcon);
        _hIcon = 0;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);
}
