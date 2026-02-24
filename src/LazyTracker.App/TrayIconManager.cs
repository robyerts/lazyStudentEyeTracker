using System.Drawing;
using System.Windows.Forms;
using LazyTracker.Core;
using Microsoft.Extensions.Hosting;

namespace LazyTracker.App;

/// <summary>
/// Manages the system tray icon on Windows.
/// Provides pause/resume and exit functionality via a context menu.
///
/// On non-Windows platforms, this class is not instantiated.
/// The core detection logic in LazyTracker.Core is fully cross-platform.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _pauseItem;
    private readonly FaceWatcher _watcher;
    private readonly IHostApplicationLifetime _lifetime;
    private bool _disposed;

    public TrayIconManager(FaceWatcher watcher, IHostApplicationLifetime lifetime)
    {
        _watcher = watcher;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Initializes the tray icon. Must be called from a thread with
    /// a Windows Forms message pump (STAThread or Application.Run context).
    /// </summary>
    public void Initialize()
    {
        _contextMenu = new ContextMenuStrip();

        _pauseItem = new ToolStripMenuItem("⏸️ Pause", null, OnPauseResumeClick);
        var exitItem = new ToolStripMenuItem("❌ Exit", null, OnExitClick);
        var statusItem = new ToolStripMenuItem("LazyTracker v1.0") { Enabled = false };

        _contextMenu.Items.Add(statusItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_pauseItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "LazyTracker - Starting...",
            Icon = CreateDefaultIcon(),
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += OnPauseResumeClick;
    }

    public void UpdateTooltip(string text)
    {
        if (_notifyIcon != null)
        {
            // NotifyIcon.Text has a 127 char limit
            _notifyIcon.Text = text.Length > 127 ? text[..127] : text;
        }
    }

    public void ShowBalloon(string title, string text, bool isWarning = false)
    {
        _notifyIcon?.ShowBalloonTip(
            3000,
            title,
            text,
            isWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void OnPauseResumeClick(object? sender, EventArgs e)
    {
        if (_watcher.IsPaused)
        {
            _watcher.Resume();
            _pauseItem!.Text = "⏸️ Pause";
            UpdateTooltip("LazyTracker - Watching 👁️");
        }
        else
        {
            _watcher.Pause();
            _pauseItem!.Text = "▶️ Resume";
            UpdateTooltip("LazyTracker - Paused ⏸️");
        }
    }

    private void OnExitClick(object? sender, EventArgs e)
    {
        _lifetime.StopApplication();
    }

    /// <summary>
    /// Creates a simple eye icon programmatically (no .ico file needed).
    /// </summary>
    private static Icon CreateDefaultIcon()
    {
        // Create a simple 16x16 icon with an "eye" shape
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);

        g.Clear(Color.Transparent);

        // Eye outline (ellipse)
        using var pen = new Pen(Color.White, 1.5f);
        g.DrawEllipse(pen, 2, 4, 12, 8);

        // Pupil
        using var brush = new SolidBrush(Color.DeepSkyBlue);
        g.FillEllipse(brush, 5, 5, 6, 6);

        // Center dot
        using var blackBrush = new SolidBrush(Color.Black);
        g.FillEllipse(blackBrush, 7, 7, 3, 3);

        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _contextMenu?.Dispose();
    }
}
