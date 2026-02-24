using OpenCvSharp;

namespace LazyTracker.Core;

/// <summary>
/// Watches the webcam for face presence using OpenCV Haar cascade detection.
/// Raises the <see cref="LookedAway"/> event when no frontal face is detected
/// for longer than the configured threshold.
/// 
/// This class is fully cross-platform — it only depends on OpenCvSharp4
/// and the appropriate platform runtime package.
/// </summary>
public sealed class FaceWatcher : IDisposable
{
    private readonly LazyTrackerOptions _options;
    private readonly CascadeClassifier _faceCascade;

    private VideoCapture? _capture;
    private Timer? _timer;
    private DateTime _lastFaceSeen;
    private DateTime _lastTriggerTime;
    private int _totalTriggers;
    private bool _isRunning;
    private bool _isPaused;
    private bool _disposed;
    private readonly object _lock = new();

    /// <summary>
    /// Fired when the user has been looking away longer than the threshold.
    /// </summary>
    public event EventHandler<LookedAwayEventArgs>? LookedAway;

    /// <summary>
    /// Fired on each detection frame with the current status.
    /// </summary>
    public event EventHandler<FaceDetectionStatus>? StatusChanged;

    /// <summary>Whether the watcher is currently running.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Whether the watcher is paused.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Total number of "looked away" triggers this session.</summary>
    public int TotalTriggers => _totalTriggers;

    public FaceWatcher(LazyTrackerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Load Haar cascade for frontal face detection
        var cascadePath = FindCascadePath();
        _faceCascade = new CascadeClassifier(cascadePath);

        if (_faceCascade.Empty())
            throw new FileNotFoundException(
                $"Failed to load Haar cascade from: {cascadePath}");
    }

    /// <summary>
    /// Starts the webcam capture and face detection loop.
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning) return;

            // Use DirectShow on Windows for more reliable camera enumeration
            _capture = OperatingSystem.IsWindows()
                ? new VideoCapture(_options.CameraIndex, VideoCaptureAPIs.DSHOW)
                : new VideoCapture(_options.CameraIndex);

            if (!_capture.IsOpened())
                throw new InvalidOperationException(
                    $"Could not open webcam at index {_options.CameraIndex}. " +
                    "Make sure a camera is connected and not in use by another app.");

            // Set a modest resolution — we don't need HD for face detection
            _capture.Set(VideoCaptureProperties.FrameWidth, 640);
            _capture.Set(VideoCaptureProperties.FrameHeight, 480);

            _lastFaceSeen = DateTime.UtcNow;
            _lastTriggerTime = DateTime.MinValue;
            _isRunning = true;
            _isPaused = false;

            var intervalMs = 1000 / Math.Max(1, _options.DetectionFps);
            _timer = new Timer(DetectionTick, null, 0, intervalMs);
        }
    }

    /// <summary>
    /// Stops the webcam capture and detection loop.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning) return;

            _timer?.Dispose();
            _timer = null;
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
            _isRunning = false;
            _isPaused = false;
        }
    }

    /// <summary>Pauses detection without releasing the camera.</summary>
    public void Pause()
    {
        _isPaused = true;
        StatusChanged?.Invoke(this, new FaceDetectionStatus
        {
            FaceDetected = false,
            State = WatcherState.Paused
        });
    }

    /// <summary>Resumes detection after a pause.</summary>
    public void Resume()
    {
        _isPaused = false;
        _lastFaceSeen = DateTime.UtcNow; // Reset timer so user isn't immediately punished
        StatusChanged?.Invoke(this, new FaceDetectionStatus
        {
            FaceDetected = true,
            State = WatcherState.Watching
        });
    }

    private void DetectionTick(object? state)
    {
        if (_isPaused || _disposed) return;

        lock (_lock)
        {
            if (!_isRunning || _capture == null || _disposed) return;

            try
            {
                using var frame = new Mat();
                if (!_capture.Read(frame) || frame.Empty())
                    return;

                // Convert to grayscale for detection
                using var gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.EqualizeHist(gray, gray);

                // Detect frontal faces
                var faces = _faceCascade.DetectMultiScale(
                    image: gray,
                    scaleFactor: 1.1,
                    minNeighbors: 5,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size(_options.MinFaceSize, _options.MinFaceSize));

                var now = DateTime.UtcNow;
                bool faceFound = faces.Length > 0;

                if (faceFound)
                {
                    _lastFaceSeen = now;
                    StatusChanged?.Invoke(this, new FaceDetectionStatus
                    {
                        FaceDetected = true,
                        State = WatcherState.Watching,
                        SecondsAway = 0
                    });
                }
                else
                {
                    var secondsAway = (now - _lastFaceSeen).TotalSeconds;

                    StatusChanged?.Invoke(this, new FaceDetectionStatus
                    {
                        FaceDetected = false,
                        State = WatcherState.Watching,
                        SecondsAway = secondsAway
                    });

                    // Check if we've exceeded the threshold
                    if (secondsAway >= _options.LookAwayThresholdSeconds)
                    {
                        // Check cooldown
                        var sinceTrigger = (now - _lastTriggerTime).TotalSeconds;
                        if (sinceTrigger >= _options.CooldownSeconds)
                        {
                            _totalTriggers++;
                            _lastTriggerTime = now;
                            _lastFaceSeen = now; // Reset so we don't immediately re-trigger

                            LookedAway?.Invoke(this, new LookedAwayEventArgs
                            {
                                SecondsAway = secondsAway,
                                TotalTriggers = _totalTriggers
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Swallow frame processing errors to keep the loop alive.
                // Camera hiccups, transient OpenCV errors, etc.
            }
        }
    }

    private static string FindCascadePath()
    {
        const string cascadeFileName = "haarcascade_frontalface_default.xml";

        // 1. Try file on disk first (for non-single-file or dev scenarios)
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", cascadeFileName),
            Path.Combine(AppContext.BaseDirectory, cascadeFileName),
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", cascadeFileName),
            Path.Combine(Directory.GetCurrentDirectory(), cascadeFileName),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        // 2. Extract from embedded resource (single-file publish scenario)
        var assembly = typeof(FaceWatcher).Assembly;
        using var stream = assembly.GetManifestResourceStream(cascadeFileName);
        if (stream != null)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "LazyTracker");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, cascadeFileName);

            // Only extract if not already cached
            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length != stream.Length)
            {
                using var fs = File.Create(tempPath);
                stream.CopyTo(fs);
            }

            return tempPath;
        }

        throw new FileNotFoundException(
            $"Could not find {cascadeFileName}. " +
            $"Searched disk: {string.Join(", ", candidates)} and embedded resources.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _faceCascade.Dispose();
    }
}

/// <summary>
/// Status snapshot emitted on each detection frame.
/// </summary>
public sealed class FaceDetectionStatus
{
    public bool FaceDetected { get; init; }
    public WatcherState State { get; init; }
    public double SecondsAway { get; init; }
}

/// <summary>
/// Current state of the face watcher.
/// </summary>
public enum WatcherState
{
    Watching,
    Paused,
    Stopped
}
