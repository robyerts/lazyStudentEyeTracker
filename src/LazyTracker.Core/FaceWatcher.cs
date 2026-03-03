using OpenCvSharp;

namespace LazyTracker.Core;

/// <summary>
/// Watches the webcam for face presence using OpenCV Haar cascade detection.
/// Detects two scenarios:
///   1. Face gone — user turned away or left the desk
///   2. Looking down — face detected but shifted significantly downward
///      from the baseline position (phone usage)
/// 
/// Uses face POSITION tracking instead of eye detection for the "looking down"
/// signal, which is far more reliable than Haar cascade eye detection.
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
    private DateTime _lastAttentiveTime;
    private DateTime _lastTriggerTime;
    private int _totalTriggers;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isAutoPaused;
    private bool _disposed;
    private readonly object _lock = new();
    private int _frameHeight;

    // Face position baseline tracking
    private double _baselineFaceCenterY = -1;
    private readonly Queue<double> _recentFaceCenterYs = new();
    private int _baselineWindowSize;

    // Grace period after resume — suppress looking-down during baseline stabilisation
    private DateTime _resumeGraceUntil = DateTime.MinValue;
    private const int ResumeGraceSeconds = 5;

    /// <summary>
    /// Fired when the user has been looking away longer than the threshold.
    /// </summary>
    public event EventHandler<LookedAwayEventArgs>? LookedAway;

    /// <summary>
    /// Fired on each detection frame with the current status.
    /// </summary>
    public event EventHandler<FaceDetectionStatus>? StatusChanged;

    /// <summary>
    /// Fired when the user returns after auto-pause (left and came back).
    /// </summary>
    public event EventHandler? UserReturned;

    /// <summary>Whether the watcher is currently running.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Whether the watcher is paused (manually or auto).</summary>
    public bool IsPaused => _isPaused || _isAutoPaused;

    /// <summary>Whether the watcher auto-paused because the user left.</summary>
    public bool IsAutoPaused => _isAutoPaused;

    /// <summary>Total number of "looked away" triggers this session.</summary>
    public int TotalTriggers => _totalTriggers;

    public FaceWatcher(LazyTrackerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Load Haar cascade for frontal face detection
        var faceCascadePath = FindCascadePath("haarcascade_frontalface_default.xml");
        _faceCascade = new CascadeClassifier(faceCascadePath);
        if (_faceCascade.Empty())
            throw new FileNotFoundException(
                $"Failed to load face cascade from: {faceCascadePath}");
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
            _frameHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
            if (_frameHeight <= 0) _frameHeight = 480;

            _lastFaceSeen = DateTime.UtcNow;
            _lastAttentiveTime = DateTime.UtcNow;
            _lastTriggerTime = DateTime.MinValue;
            _baselineFaceCenterY = -1;
            _recentFaceCenterYs.Clear();
            _baselineWindowSize = Math.Max(5, _options.DetectionFps * 3); // 3 seconds of baseline
            _isRunning = true;
            _isPaused = false;
            _isAutoPaused = false;

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
        ResetBaseline();
        StatusChanged?.Invoke(this, new FaceDetectionStatus
        {
            FaceDetected = true,
            State = WatcherState.Watching
        });
    }

    /// <summary>Resets tracking state and optionally preserves the baseline.</summary>
    private void ResetBaseline(bool preserveBaseline = false)
    {
        _lastFaceSeen = DateTime.UtcNow;
        _lastAttentiveTime = DateTime.UtcNow;
        if (!preserveBaseline)
        {
            _baselineFaceCenterY = -1;
            _recentFaceCenterYs.Clear();
        }
        _resumeGraceUntil = DateTime.UtcNow.AddSeconds(ResumeGraceSeconds);
    }

    private void DetectionTick(object? state)
    {
        if (_isPaused || _disposed) return;

        // When auto-paused, still look for face to detect the user returning
        if (_isAutoPaused)
        {
            DetectReturnFromAutoPause();
            return;
        }

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
                    minNeighbors: 4,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size(_options.MinFaceSize, _options.MinFaceSize));

                var now = DateTime.UtcNow;
                bool faceFound = faces.Length > 0;

                if (faceFound)
                {
                    _lastFaceSeen = now;

                    // Get the largest face (most likely the user)
                    var face = faces.OrderByDescending(f => f.Width * f.Height).First();
                    double faceCenterY = face.Y + face.Height / 2.0;

                    // Normalize to frame height (0.0 = top, 1.0 = bottom)
                    double normalizedY = faceCenterY / _frameHeight;

                    // Track position for baseline calculation
                    _recentFaceCenterYs.Enqueue(normalizedY);
                    while (_recentFaceCenterYs.Count > _baselineWindowSize)
                        _recentFaceCenterYs.Dequeue();

                    // Establish baseline from the median of recent positions
                    // (median is more robust to outliers than mean)
                    if (_baselineFaceCenterY < 0 && _recentFaceCenterYs.Count >= 5)
                    {
                        _baselineFaceCenterY = GetMedian(_recentFaceCenterYs);
                    }

                    // Check if face has shifted significantly downward
                    // (suppressed during grace period after resume)
                    bool isLookingDown = false;
                    if (_baselineFaceCenterY > 0 && now > _resumeGraceUntil)
                    {
                        double dropAmount = normalizedY - _baselineFaceCenterY;
                        // Drop threshold as % of frame height (default 0.12 = ~58px in 480p)
                        isLookingDown = dropAmount > _options.FaceDropThreshold;

                        // Slowly adapt baseline upward if face is at or above baseline
                        // (handles user shifting in chair over time)
                        if (!isLookingDown)
                        {
                            _baselineFaceCenterY = _baselineFaceCenterY * 0.98 + normalizedY * 0.02;
                        }
                    }

                    if (!isLookingDown)
                    {
                        _lastAttentiveTime = now;
                        StatusChanged?.Invoke(this, new FaceDetectionStatus
                        {
                            FaceDetected = true,
                            IsLookingDown = false,
                            State = WatcherState.Watching,
                            SecondsAway = 0
                        });
                    }
                    else
                    {
                        var secondsDown = (now - _lastAttentiveTime).TotalSeconds;

                        StatusChanged?.Invoke(this, new FaceDetectionStatus
                        {
                            FaceDetected = true,
                            IsLookingDown = true,
                            State = WatcherState.Watching,
                            SecondsAway = secondsDown
                        });

                        if (secondsDown >= _options.LookingDownThresholdSeconds)
                        {
                            var sinceTrigger = (now - _lastTriggerTime).TotalSeconds;
                            if (sinceTrigger >= _options.CooldownSeconds)
                            {
                                _totalTriggers++;
                                _lastTriggerTime = now;
                                _lastAttentiveTime = now;

                                LookedAway?.Invoke(this, new LookedAwayEventArgs
                                {
                                    SecondsAway = secondsDown,
                                    TotalTriggers = _totalTriggers,
                                    Reason = LookAwayReason.LookingDown
                                });
                            }
                        }
                    }
                }
                else
                {
                    // No face at all — turned away or left
                    var secondsAway = (now - _lastFaceSeen).TotalSeconds;

                    // Check if user has been gone long enough to auto-pause
                    if (_options.AutoPauseSeconds > 0 && secondsAway >= _options.AutoPauseSeconds)
                    {
                        _isAutoPaused = true;
                        StatusChanged?.Invoke(this, new FaceDetectionStatus
                        {
                            FaceDetected = false,
                            IsLookingDown = false,
                            State = WatcherState.AutoPaused,
                            SecondsAway = secondsAway
                        });
                        return;
                    }

                    StatusChanged?.Invoke(this, new FaceDetectionStatus
                    {
                        FaceDetected = false,
                        IsLookingDown = false,
                        State = WatcherState.Watching,
                        SecondsAway = secondsAway
                    });

                    if (secondsAway >= _options.LookAwayThresholdSeconds)
                    {
                        var sinceTrigger = (now - _lastTriggerTime).TotalSeconds;
                        if (sinceTrigger >= _options.CooldownSeconds)
                        {
                            _totalTriggers++;
                            _lastTriggerTime = now;
                            _lastFaceSeen = now;

                            LookedAway?.Invoke(this, new LookedAwayEventArgs
                            {
                                SecondsAway = secondsAway,
                                TotalTriggers = _totalTriggers,
                                Reason = LookAwayReason.FaceGone
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

    private void DetectReturnFromAutoPause()
    {
        lock (_lock)
        {
            if (!_isRunning || _capture == null || _disposed) return;

            try
            {
                using var frame = new Mat();
                if (!_capture.Read(frame) || frame.Empty())
                    return;

                using var gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.EqualizeHist(gray, gray);

                var faces = _faceCascade.DetectMultiScale(
                    image: gray,
                    scaleFactor: 1.1,
                    minNeighbors: 4,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size(_options.MinFaceSize, _options.MinFaceSize));

                if (faces.Length > 0)
                {
                    // User is back! Resume with the saved baseline position.
                    _isAutoPaused = false;
                    ResetBaseline(preserveBaseline: true);

                    StatusChanged?.Invoke(this, new FaceDetectionStatus
                    {
                        FaceDetected = true,
                        IsLookingDown = false,
                        State = WatcherState.Watching,
                        SecondsAway = 0
                    });

                    UserReturned?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception)
            {
                // Swallow frame errors
            }
        }
    }

    private static double GetMedian(Queue<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private static string FindCascadePath(string cascadeFileName)
    {

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
    public bool IsLookingDown { get; init; }
    public WatcherState State { get; init; }
    public double SecondsAway { get; init; }
}

/// <summary>
/// Why the user was detected as not paying attention.
/// </summary>
public enum LookAwayReason
{
    /// <summary>Face left the frame entirely (turned away, left desk).</summary>
    FaceGone,

    /// <summary>Face dropped significantly below baseline position (looking at phone).</summary>
    LookingDown
}

/// <summary>
/// Current state of the face watcher.
/// </summary>
public enum WatcherState
{
    Watching,
    Paused,
    AutoPaused,
    Stopped
}
