namespace LazyTracker.Core;

/// <summary>
/// Configuration options for the LazyTracker focus monitor.
/// </summary>
public sealed class LazyTrackerOptions
{
    /// <summary>
    /// Number of seconds the user must look away before triggering the browser.
    /// </summary>
    public int LookAwayThresholdSeconds { get; set; } = 5;

    /// <summary>
    /// Cooldown in seconds after a trigger before another trigger can fire.
    /// Prevents browser-tab spam.
    /// </summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>
    /// The URL to open when the user looks away too long.
    /// </summary>
    public string TargetUrl { get; set; } = "https://www.mcdonalds.ro/cariere";

    /// <summary>
    /// Webcam device index (0 = default camera).
    /// </summary>
    public int CameraIndex { get; set; } = 0;

    /// <summary>
    /// How many frames per second to capture for face detection.
    /// Higher = more responsive but more CPU. 5 is a good balance.
    /// </summary>
    public int DetectionFps { get; set; } = 5;

    /// <summary>
    /// Minimum face size in pixels for detection. Smaller values detect
    /// faces further from the camera but may produce false positives.
    /// </summary>
    public int MinFaceSize { get; set; } = 80;
}
