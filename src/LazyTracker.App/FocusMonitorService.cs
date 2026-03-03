using LazyTracker.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LazyTracker.App;

/// <summary>
/// Background service that runs the face watcher and triggers
/// the browser when the user looks away too long.
/// </summary>
public sealed class FocusMonitorService : BackgroundService
{
    private readonly FaceWatcher _watcher;
    private readonly LazyTrackerOptions _options;
    private readonly ILogger<FocusMonitorService> _logger;
    private readonly TrayIconManager? _trayManager;

    public FocusMonitorService(
        FaceWatcher watcher,
        LazyTrackerOptions options,
        ILogger<FocusMonitorService> logger,
        TrayIconManager? trayManager = null)
    {
        _watcher = watcher;
        _options = options;
        _logger = logger;
        _trayManager = trayManager;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LazyTracker Focus Monitor starting...");
        _logger.LogInformation("Threshold: {Seconds}s | Cooldown: {Cooldown}s | Target: {Url}",
            _options.LookAwayThresholdSeconds,
            _options.CooldownSeconds,
            _options.TargetUrl);

        // Wire up the "looked away" event → open browser
        _watcher.LookedAway += OnLookedAway;
        _watcher.StatusChanged += OnStatusChanged;
        _watcher.UserReturned += OnUserReturned;

        try
        {
            _watcher.Start();
            _logger.LogInformation("Webcam opened. Face detection active. Stay focused!");
            _trayManager?.UpdateTooltip("LazyTracker - Watching 👁️");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start face watcher");
            throw;
        }

        // Register cleanup on cancellation
        stoppingToken.Register(() =>
        {
            _logger.LogInformation("Stopping face watcher...");
            _watcher.LookedAway -= OnLookedAway;
            _watcher.StatusChanged -= OnStatusChanged;
            _watcher.UserReturned -= OnUserReturned;
            _watcher.Stop();
        });

        // The watcher runs on its own timer, so we just wait for cancellation
        return Task.CompletedTask;
    }

    private void OnLookedAway(object? sender, LookedAwayEventArgs e)
    {
        var reasonText = e.Reason == LookAwayReason.LookingDown
            ? "LOOKING AT PHONE"
            : "LOOKED AWAY";

        _logger.LogWarning(
            "👀 {Reason} for {Seconds:F1}s! Opening McDonald's careers... (trigger #{Count})",
            reasonText, e.SecondsAway, e.TotalTriggers);

        BrowserLauncher.OpenUrl(_options.TargetUrl);

        var balloonText = e.Reason == LookAwayReason.LookingDown
            ? $"Put your phone down! ({e.SecondsAway:F0}s). Time #{e.TotalTriggers}."
            : $"You looked away for {e.SecondsAway:F0}s. Time #{e.TotalTriggers}. Focus!";

        _trayManager?.ShowBalloon(
            "Get back to studying!",
            balloonText,
            isWarning: true);
    }

    private void OnStatusChanged(object? sender, FaceDetectionStatus status)
    {
        if (status.State == WatcherState.Paused)
        {
            _trayManager?.UpdateTooltip("LazyTracker - Paused ⏸️");
        }
        else if (status.State == WatcherState.AutoPaused)
        {
            _logger.LogInformation("User left — auto-paused detection.");
            _trayManager?.UpdateTooltip("LazyTracker - Away (auto-paused) 💤");
            _trayManager?.ShowBalloon(
                "Auto-paused 💤",
                "You left your laptop. Detection paused until you return.",
                isWarning: false);
        }
        else if (status.FaceDetected && !status.IsLookingDown)
        {
            _trayManager?.UpdateTooltip("LazyTracker - Watching 👁️ (focused)");
        }
        else if (status.FaceDetected && status.IsLookingDown)
        {
            _trayManager?.UpdateTooltip($"LazyTracker - Looking down! ({status.SecondsAway:F0}s)");
        }
        else if (status.SecondsAway > 0)
        {
            _trayManager?.UpdateTooltip($"LazyTracker - No face! ({status.SecondsAway:F0}s)");
        }
    }

    private void OnUserReturned(object? sender, EventArgs e)
    {
        _logger.LogInformation("Welcome back! Detection resumed.");
        _trayManager?.UpdateTooltip("LazyTracker - Watching 👁️ (focused)");
        _trayManager?.ShowBalloon(
            "Welcome back! 📚",
            "Detection resumed. Time to focus on your studies!",
            isWarning: false);
    }
}
