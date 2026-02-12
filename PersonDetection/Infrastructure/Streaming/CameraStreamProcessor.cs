// PersonDetection.Infrastructure/Streaming/CameraStreamProcessor.cs
namespace PersonDetection.Infrastructure.Streaming
{
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading.Channels;
    using Microsoft.AspNetCore.SignalR;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using OpenCvSharp;
    using PersonDetection.API.Hubs;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Services;
    using PersonDetection.Domain.ValueObjects;
    using PersonDetection.Infrastructure.Context;
    using PersonDetection.Infrastructure.Detection;
    using PersonDetection.Infrastructure.ReId;

    using CvPoint = OpenCvSharp.Point;
    using CvSize = OpenCvSharp.Size;

    public class CameraStreamProcessor : IStreamProcessor
    {
        private readonly int _cameraId;
        private readonly IDetectionEngine<YoloDetectionConfig> _detectionEngine;
        private readonly IReIdentificationEngine<OSNetConfig>? _reidEngine;
        private readonly IPersonIdentityMatcher _identityMatcher;
        private readonly IHubContext<DetectionHub> _hubContext;
        private readonly IServiceProvider _serviceProvider;
        private readonly StreamingSettings _settings;
        private readonly DetectionSettings _detectionSettings;
        private readonly PersistenceSettings _persistenceSettings;
        private readonly ILogger<CameraStreamProcessor> _logger;

        // Thread-safe VideoCapture access
        private VideoCapture? _capture;
        private readonly SemaphoreSlim _captureLock = new(1, 1);
        private CancellationTokenSource? _cts;

        private Task? _captureTask;
        private Task? _streamTask;
        private Task? _detectionTask;
        private Task? _saveTask;

        private readonly Channel<byte[]> _streamChannel;

        // Thread-safe frame storage
        private readonly object _frameLock = new();
        private byte[]? _currentJpeg;
        private int _frameWidth = 1280;
        private int _frameHeight = 720;

        // Thread-safe detection storage
        private readonly object _detectionLock = new();
        private List<TrackedPerson> _currentTrackedPersons = new();
        private int _uniquePersonCount;
        private readonly HashSet<Guid> _seenPersonIds = new();

        // Database save state
        private DateTime _lastDbSave = DateTime.MinValue;
        private int _lastSavedCount = -1;

        // Performance
        private int _frameCount;
        private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
        private double _currentFps;

        private volatile bool _disposed;
        private volatile bool _isConnected;

        // Pre-computed colors for faster rendering
        private static readonly Scalar[] _personColors;
        private static readonly Scalar _yellow = new(0, 255, 255);
        private static readonly Scalar _white = new(255, 255, 255);
        private static readonly Scalar _green = new(0, 255, 0);
        private static readonly Scalar _cyan = new(255, 255, 0);
        private static readonly Scalar _black = new(0, 0, 0);
        private static readonly Scalar _overlayBg = new(0, 0, 0, 180);

        static CameraStreamProcessor()
        {
            // Pre-generate 100 distinct colors
            var rand = new Random(42);
            _personColors = new Scalar[100];
            for (int i = 0; i < 100; i++)
            {
                _personColors[i] = new Scalar(
                    rand.Next(100, 255),
                    rand.Next(100, 255),
                    rand.Next(100, 255));
            }
        }

        public bool IsConnected => _isConnected;
        public int CurrentPersonCount => _currentTrackedPersons.Count;
        public int UniquePersonCount => _uniquePersonCount;
        public string StreamUrl { get; private set; } = string.Empty;

        private class TrackedPerson
        {
            public Guid GlobalPersonId { get; set; }
            public BoundingBox BoundingBox { get; set; } = null!;
            public float Confidence { get; set; }
            public float[]? Features { get; set; }
            public bool IsNew { get; set; }
            public bool HasReIdMatch { get; set; } // NEW: Track if matched via ReID
            public int TrackId { get; set; } // NEW: For stability tracking
            public int StableFrameCount { get; set; } // NEW: How many frames with same ID
        }

        // NEW: Stable track management
        private readonly Dictionary<int, StableTrack> _stableTracks = new();
        private int _nextTrackId = 1;

        private class StableTrack
        {
            public int TrackId { get; set; }
            public Guid GlobalPersonId { get; set; }
            public BoundingBox LastBox { get; set; } = null!;
            public DateTime LastSeen { get; set; }
            public int ConsecutiveFrames { get; set; }
            public float[]? Features { get; set; }
            public float LastConfidence { get; set; }
        }

        public CameraStreamProcessor(
            int cameraId,
            IDetectionEngine<YoloDetectionConfig> detectionEngine,
            IReIdentificationEngine<OSNetConfig>? reidEngine,
            IPersonIdentityMatcher identityMatcher,
            IHubContext<DetectionHub> hubContext,
            IServiceProvider serviceProvider,
            IOptions<StreamingSettings> streamingSettings,
            IOptions<DetectionSettings> detectionSettings,
            IOptions<PersistenceSettings> persistenceSettings,
            ILogger<CameraStreamProcessor> logger)
        {
            _cameraId = cameraId;
            _detectionEngine = detectionEngine;
            _reidEngine = reidEngine;
            _identityMatcher = identityMatcher;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;
            _settings = streamingSettings.Value;
            _detectionSettings = detectionSettings.Value;
            _persistenceSettings = persistenceSettings.Value;
            _logger = logger;

            _streamChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });
        }

        public async Task<bool> ConnectAsync(string url, CancellationToken ct = default)
        {
            StreamUrl = url;

            for (int attempt = 1; attempt <= _settings.MaxReconnectAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    _isConnected = await ConnectWithOpenCvAsync(url, ct);

                    if (_isConnected)
                    {
                        // Set frame dimensions for entry zone detection
                        _identityMatcher.SetFrameDimensions(_frameWidth, _frameHeight);

                        StartAllTasks();
                        _logger.LogInformation("✅ Camera {Id} connected to {Url} ({W}x{H})",
                            _cameraId, url, _frameWidth, _frameHeight);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Connection attempt {Attempt}/{Max} failed",
                        attempt, _settings.MaxReconnectAttempts);
                }

                if (attempt < _settings.MaxReconnectAttempts)
                {
                    await Task.Delay(_settings.ReconnectDelayMs, ct);
                }
            }

            _logger.LogError("❌ Failed to connect to camera {Id}", _cameraId);
            return false;
        }

        private async Task<bool> ConnectWithOpenCvAsync(string url, CancellationToken ct)
        {
            return await Task.Run(async () =>
            {
                await _captureLock.WaitAsync(ct);
                try
                {
                    _capture?.Dispose();
                    _capture = null;

                    // Webcam index
                    if (int.TryParse(url, out int cameraIndex))
                    {
                        _logger.LogInformation("Opening webcam at index {Index}", cameraIndex);

                        var backends = new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY };
                        foreach (var backend in backends)
                        {
                            try
                            {
                                _capture = new VideoCapture(cameraIndex, backend);
                                if (_capture.IsOpened())
                                {
                                    ConfigureCapture();
                                    _logger.LogInformation("Webcam opened with {Backend}", backend);
                                    return true;
                                }
                                _capture.Dispose();
                            }
                            catch { _capture?.Dispose(); }
                        }
                        return false;
                    }

                    // HTTP/RTSP/File - use FFMPEG
                    _logger.LogInformation("Opening stream via FFMPEG: {Url}", url);

                    Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS",
                        "rtsp_transport;tcp|buffer_size;1024000|max_delay;500000");

                    _capture = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);

                    if (_capture.IsOpened())
                    {
                        ConfigureCapture();
                        return true;
                    }

                    _capture.Dispose();
                    _capture = new VideoCapture(url, VideoCaptureAPIs.ANY);

                    if (_capture.IsOpened())
                    {
                        ConfigureCapture();
                        return true;
                    }

                    _capture?.Dispose();
                    _capture = null;
                    return false;
                }
                finally
                {
                    _captureLock.Release();
                }
            }, ct);
        }

        private void ConfigureCapture()
        {
            if (_capture == null) return;

            try
            {
                _capture.Set(VideoCaptureProperties.BufferSize, 1);
                _capture.Set(VideoCaptureProperties.FrameWidth, _settings.ResizeWidth);
                _capture.Set(VideoCaptureProperties.FrameHeight, _settings.ResizeHeight);

                _frameWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                _frameHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                var fps = _capture.Get(VideoCaptureProperties.Fps);

                if (_frameWidth <= 0) _frameWidth = _settings.ResizeWidth;
                if (_frameHeight <= 0) _frameHeight = _settings.ResizeHeight;

                _logger.LogInformation("Camera {Id}: {Width}x{Height} @ {Fps} FPS",
                    _cameraId, _frameWidth, _frameHeight, fps);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to configure capture");
            }
        }

        private void StartAllTasks()
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _captureTask = Task.Run(() => SafeCaptureLoop(ct), ct);
            _streamTask = Task.Run(() => OptimizedStreamOutputLoop(ct), ct);
            _detectionTask = Task.Run(() => OptimizedDetectionLoop(ct), ct);
            _saveTask = Task.Run(() => BatchDatabaseSaveLoop(ct), ct);

            _logger.LogInformation("All tasks started for camera {Id}", _cameraId);
        }

        private async Task SafeCaptureLoop(CancellationToken ct)
        {
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _settings.TargetFps);
            int consecutiveErrors = 0;
            const int MaxConsecutiveErrors = 30;

            _logger.LogInformation("🎥 Capture loop started for camera {Id}", _cameraId);

            while (!ct.IsCancellationRequested && !_disposed)
            {
                if (!_isConnected)
                {
                    break;
                }

                try
                {
                    var startTime = DateTime.UtcNow;
                    byte[]? jpegData = null;

                    if (!await _captureLock.WaitAsync(1000, ct))
                    {
                        continue;
                    }

                    try
                    {
                        if (_capture == null || !_capture.IsOpened())
                        {
                            consecutiveErrors++;
                            if (consecutiveErrors > MaxConsecutiveErrors)
                            {
                                _isConnected = false;
                                break;
                            }
                            await Task.Delay(100, ct);
                            continue;
                        }

                        using var frame = new Mat();
                        bool success = _capture.Read(frame);

                        if (!success || frame.Empty())
                        {
                            consecutiveErrors++;
                            await Task.Delay(50, ct);
                            continue;
                        }

                        consecutiveErrors = 0;

                        // Update frame dimensions if changed
                        if (frame.Width != _frameWidth || frame.Height != _frameHeight)
                        {
                            _frameWidth = frame.Width;
                            _frameHeight = frame.Height;
                            _identityMatcher.SetFrameDimensions(_frameWidth, _frameHeight);
                        }

                        Cv2.ImEncode(".jpg", frame, out jpegData, new[]
                        {
                            (int)ImwriteFlags.JpegQuality, _settings.JpegQuality
                        });
                    }
                    finally
                    {
                        _captureLock.Release();
                    }

                    if (jpegData != null && jpegData.Length > 1000)
                    {
                        ProcessFrame(jpegData);
                    }

                    var elapsed = DateTime.UtcNow - startTime;
                    if (elapsed < frameInterval)
                    {
                        await Task.Delay(frameInterval - elapsed, ct);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Capture error");
                    consecutiveErrors++;
                    if (consecutiveErrors > MaxConsecutiveErrors)
                    {
                        _isConnected = false;
                        break;
                    }
                    try { await Task.Delay(100, ct); } catch { break; }
                }
            }

            _logger.LogInformation("🛑 Capture loop ended for camera {Id}", _cameraId);
        }

        private void ProcessFrame(byte[] jpeg)
        {
            _frameCount++;

            if (_fpsWatch.ElapsedMilliseconds >= 1000)
            {
                _currentFps = _frameCount * 1000.0 / _fpsWatch.ElapsedMilliseconds;
                _frameCount = 0;
                _fpsWatch.Restart();
            }

            lock (_frameLock)
            {
                _currentJpeg = jpeg;
            }
        }

        /// <summary>
        /// OPTIMIZED stream output with pre-rendered overlay
        /// </summary>
        private async Task OptimizedStreamOutputLoop(CancellationToken ct)
        {
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _settings.TargetFps);
            var encodeParams = new[] { (int)ImwriteFlags.JpegQuality, _settings.JpegQuality };

            while (!ct.IsCancellationRequested && _isConnected && !_disposed)
            {
                try
                {
                    await Task.Delay(frameInterval, ct);

                    byte[]? jpeg;
                    lock (_frameLock) { jpeg = _currentJpeg; }
                    if (jpeg == null) continue;

                    List<TrackedPerson> persons;
                    lock (_detectionLock) { persons = _currentTrackedPersons.ToList(); }

                    // Decode frame
                    using var frame = Mat.FromImageData(jpeg, ImreadModes.Color);
                    if (frame.Empty()) continue;

                    // Fast annotation drawing
                    DrawOptimizedAnnotations(frame, persons);

                    // Encode and send
                    Cv2.ImEncode(".jpg", frame, out var annotatedJpeg, encodeParams);
                    _streamChannel.Writer.TryWrite(annotatedJpeg);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stream output error");
                }
            }
        }

        /// <summary>
        /// OPTIMIZED detection loop with proper confidence passing
        /// </summary>
        private async Task OptimizedDetectionLoop(CancellationToken ct)
        {
            var detectionInterval = TimeSpan.FromMilliseconds(_settings.DetectionIntervalMs);
            var config = new YoloDetectionConfig
            {
                ConfidenceThreshold = _detectionSettings.ConfidenceThreshold,
                NmsThreshold = _detectionSettings.NmsThreshold,
                ModelInputSize = _detectionSettings.ModelInputSize
            };

            var reidConfig = new OSNetConfig();
            int framesSinceLastReId = 0;
            var reIdInterval = Math.Max(1, _settings.ReIdEveryNFrames);

            // Confirmation tracking - REDUCED to 2 frames
            var pendingConfirmations = new Dictionary<Guid, PendingPerson>();
            const int RequiredConfirmationFrames = 2; // CHANGED from 3 to 2
            const int MaxPendingAge = 8;

            while (!ct.IsCancellationRequested && _isConnected && !_disposed)
            {
                try
                {
                    await Task.Delay(detectionInterval, ct);

                    byte[]? jpeg;
                    lock (_frameLock) { jpeg = _currentJpeg; }
                    if (jpeg == null) continue;

                    // Run YOLO detection
                    var detections = await _detectionEngine.DetectAsync(jpeg, config, ct);

                    // Filter by minimum size
                    detections = detections
                        .Where(d => d.BoundingBox.Width >= _detectionSettings.MinWidth &&
                                   d.BoundingBox.Height >= _detectionSettings.MinHeight)
                        .ToList();

                    var trackedPersons = new List<TrackedPerson>();
                    framesSinceLastReId++;

                    bool shouldRunReId = _reidEngine != null &&
                                         detections.Count > 0 &&
                                         framesSinceLastReId >= reIdInterval;

                    var seenThisFrame = new HashSet<Guid>();
                    var now = DateTime.UtcNow;

                    // Clean up old tracks
                    CleanupStaleTracks(now);

                    foreach (var detection in detections)
                    {
                        var tracked = new TrackedPerson
                        {
                            BoundingBox = detection.BoundingBox,
                            Confidence = detection.Confidence,
                            GlobalPersonId = Guid.Empty,
                            HasReIdMatch = false
                        };

                        // Step 1: Find existing stable track by position
                        var existingTrack = FindExistingTrack(detection.BoundingBox);
                        if (existingTrack != null)
                        {
                            tracked.TrackId = existingTrack.TrackId;
                            tracked.GlobalPersonId = existingTrack.GlobalPersonId;
                            tracked.Features = existingTrack.Features;
                        }
                        else
                        {
                            tracked.TrackId = _nextTrackId++;
                        }

                        // Step 2: Run ReID (THIS IS THE KEY FIX)
                        if (shouldRunReId)
                        {
                            try
                            {
                                var cropArea = detection.BoundingBox.Width * detection.BoundingBox.Height;
                                var minArea = 20 * 40; // Minimum crop area

                                if (cropArea >= minArea)
                                {
                                    var featureVector = await _reidEngine!.ExtractFeaturesAsync(
                                        jpeg, detection.BoundingBox, reidConfig, ct);

                                    tracked.Features = featureVector.Values;

                                    // ★★★ KEY FIX: Pass confidence and trackId to identity matcher ★★★
                                    var reidId = _identityMatcher.GetOrCreateIdentity(
                                        featureVector,
                                        _cameraId,
                                        detection.BoundingBox,
                                        detection.Confidence,  // ← NOW PASSING CONFIDENCE
                                        tracked.TrackId);      // ← NOW PASSING TRACK ID

                                    if (reidId != Guid.Empty)
                                    {
                                        tracked.GlobalPersonId = reidId;
                                        tracked.HasReIdMatch = true;

                                        // Update stable track with ReID result
                                        UpdateStableTrack(tracked, now);

                                        _logger.LogDebug(
                                            "🔍 ReID: Track {Track} → {Id} (conf={Conf:P0})",
                                            tracked.TrackId, reidId.ToString()[..8], detection.Confidence);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "ReID failed for track {Track}", tracked.TrackId);
                            }
                        }

                        // Step 3: If no ReID match, use stable track's ID
                        if (tracked.GlobalPersonId == Guid.Empty && existingTrack != null)
                        {
                            tracked.GlobalPersonId = existingTrack.GlobalPersonId;
                        }

                        // Step 4: Create new tracking ID if still empty
                        if (tracked.GlobalPersonId == Guid.Empty)
                        {
                            tracked.GlobalPersonId = Guid.NewGuid();
                            _logger.LogDebug("🆕 New track: {Track} → {Id}",
                                tracked.TrackId, tracked.GlobalPersonId.ToString()[..8]);
                        }

                        // Always update stable track
                        UpdateStableTrack(tracked, now);

                        // Confirmation logic for unique counting
                        if (!_seenPersonIds.Contains(tracked.GlobalPersonId))
                        {
                            if (!pendingConfirmations.ContainsKey(tracked.GlobalPersonId))
                            {
                                pendingConfirmations[tracked.GlobalPersonId] = new PendingPerson
                                {
                                    FirstSeen = now,
                                    FrameCount = 0,
                                    HasFeatures = false,
                                    MaxConfidence = 0
                                };
                            }

                            var pending = pendingConfirmations[tracked.GlobalPersonId];
                            pending.FrameCount++;
                            pending.LastSeen = now;
                            pending.HasFeatures |= (tracked.Features != null);
                            pending.MaxConfidence = Math.Max(pending.MaxConfidence, detection.Confidence);

                            // ★★★ KEY FIX: Relaxed confirmation logic ★★★
                            bool shouldConfirm = false;

                            // Method 1: Standard frame count (reduced to 2)
                            if (pending.FrameCount >= RequiredConfirmationFrames && pending.HasFeatures)
                            {
                                shouldConfirm = true;
                            }

                            // Method 2: High confidence fast-track (2 frames with >55% confidence)
                            if (pending.FrameCount >= 2 && pending.MaxConfidence >= 0.55f)
                            {
                                shouldConfirm = true;
                            }

                            // Method 3: Very high confidence (1 frame with >75% + features)
                            if (detection.Confidence >= 0.75f && tracked.Features != null)
                            {
                                shouldConfirm = true;
                            }

                            if (shouldConfirm)
                            {
                                _seenPersonIds.Add(tracked.GlobalPersonId);
                                pendingConfirmations.Remove(tracked.GlobalPersonId);
                                tracked.IsNew = true;

                                _logger.LogInformation(
                                    "✅ CONFIRMED: {Id} (frames={F}, conf={C:P0}, features={Feat})",
                                    tracked.GlobalPersonId.ToString()[..8],
                                    pending.FrameCount,
                                    pending.MaxConfidence,
                                    pending.HasFeatures);
                            }
                        }

                        seenThisFrame.Add(tracked.GlobalPersonId);
                        trackedPersons.Add(tracked);
                    }

                    if (shouldRunReId)
                    {
                        framesSinceLastReId = 0;

                        // Clean up stale pending confirmations
                        var staleIds = pendingConfirmations
                            .Where(kvp => !seenThisFrame.Contains(kvp.Key) ||
                                          kvp.Value.FrameCount > MaxPendingAge ||
                                          (now - kvp.Value.LastSeen).TotalSeconds > 3)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var id in staleIds)
                        {
                            pendingConfirmations.Remove(id);
                        }
                    }

                    lock (_detectionLock)
                    {
                        _currentTrackedPersons = trackedPersons;
                        _uniquePersonCount = _identityMatcher.GetCameraIdentityCount(_cameraId);
                    }

                    await NotifyClientsAsync(trackedPersons);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Detection loop error");
                }
            }
        }

        #region Stable Track Management

        private StableTrack? FindExistingTrack(BoundingBox newBox)
        {
            StableTrack? best = null;
            float bestScore = float.MaxValue;
            var maxDistance = 120f; // Max pixel movement between frames

            foreach (var track in _stableTracks.Values)
            {
                var dx = (newBox.X + newBox.Width / 2f) - (track.LastBox.X + track.LastBox.Width / 2f);
                var dy = (newBox.Y + newBox.Height / 2f) - (track.LastBox.Y + track.LastBox.Height / 2f);
                var centerDist = (float)Math.Sqrt(dx * dx + dy * dy);

                if (centerDist > maxDistance) continue;

                // Size similarity bonus
                var sizeRatio = Math.Min(
                    (float)newBox.Width / track.LastBox.Width,
                    (float)track.LastBox.Width / newBox.Width) *
                    Math.Min(
                    (float)newBox.Height / track.LastBox.Height,
                    (float)track.LastBox.Height / newBox.Height);

                var score = centerDist / Math.Max(sizeRatio, 0.5f);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = track;
                }
            }

            return best;
        }

        private void UpdateStableTrack(TrackedPerson person, DateTime now)
        {
            if (!_stableTracks.TryGetValue(person.TrackId, out var track))
            {
                track = new StableTrack { TrackId = person.TrackId };
                _stableTracks[person.TrackId] = track;
            }

            track.GlobalPersonId = person.GlobalPersonId;
            track.LastBox = person.BoundingBox;
            track.LastSeen = now;
            track.ConsecutiveFrames++;
            track.Features = person.Features ?? track.Features;
            track.LastConfidence = person.Confidence;
        }

        private void CleanupStaleTracks(DateTime now)
        {
            var staleTrackIds = _stableTracks
                .Where(kvp => (now - kvp.Value.LastSeen).TotalSeconds > 2)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in staleTrackIds)
            {
                _stableTracks.Remove(id);
            }
        }

        #endregion

        private class PendingPerson
        {
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public int FrameCount { get; set; }
            public bool HasFeatures { get; set; }
            public float MaxConfidence { get; set; }
        }

        /// <summary>
        /// Batch database save for better performance
        /// </summary>
        private async Task BatchDatabaseSaveLoop(CancellationToken ct)
        {
            var saveInterval = TimeSpan.FromSeconds(_persistenceSettings.SaveIntervalSeconds);
            var personsToSave = new List<TrackedPerson>();

            while (!ct.IsCancellationRequested && _isConnected && !_disposed)
            {
                try
                {
                    await Task.Delay(saveInterval, ct);
                    if (!_persistenceSettings.SaveToDatabase) continue;

                    List<TrackedPerson> persons;
                    lock (_detectionLock) { persons = _currentTrackedPersons.ToList(); }

                    // Only save persons with features (confirmed by ReID)
                    var confirmedPersons = persons.Where(p => p.Features != null).ToList();

                    if (confirmedPersons.Count > 0 || _lastSavedCount != 0)
                    {
                        await BatchSaveToDatabaseAsync(confirmedPersons, ct);
                        _lastSavedCount = confirmedPersons.Count;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DB save error");
                }
            }
        }

        private async Task BatchSaveToDatabaseAsync(List<TrackedPerson> persons, CancellationToken ct)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();
                context.Database.SetCommandTimeout(10);

                // Create detection result
                var result = new DetectionResult
                {
                    CameraId = _cameraId,
                    Timestamp = DateTime.UtcNow,
                    TotalDetections = persons.Count,
                    ValidDetections = persons.Count(p => p.Confidence >= _detectionSettings.ConfidenceThreshold),
                    UniquePersonCount = _uniquePersonCount
                };
                context.DetectionResults.Add(result);
                await context.SaveChangesAsync(ct);

                // Batch process persons
                foreach (var person in persons)
                {
                    var uniquePerson = await context.UniquePersons
                        .FirstOrDefaultAsync(u => u.GlobalPersonId == person.GlobalPersonId, ct);

                    if (uniquePerson == null)
                    {
                        uniquePerson = UniquePerson.Create(person.GlobalPersonId, _cameraId, person.Features);
                        context.UniquePersons.Add(uniquePerson);
                        await context.SaveChangesAsync(ct);
                        _identityMatcher.SetDbId(person.GlobalPersonId, uniquePerson.Id);
                    }
                    else
                    {
                        uniquePerson.UpdateLastSeen(_cameraId, person.Features);
                    }

                    context.PersonSightings.Add(PersonSighting.Create(
                        uniquePerson.Id, _cameraId, result.Id, person.Confidence,
                        person.BoundingBox.X, person.BoundingBox.Y,
                        person.BoundingBox.Width, person.BoundingBox.Height));

                    context.DetectedPersons.Add(new DetectedPerson
                    {
                        GlobalPersonId = person.GlobalPersonId,
                        Confidence = person.Confidence,
                        BoundingBox_X = person.BoundingBox.X,
                        BoundingBox_Y = person.BoundingBox.Y,
                        BoundingBox_Width = person.BoundingBox.Width,
                        BoundingBox_Height = person.BoundingBox.Height,
                        FeatureVector = person.Features != null ? string.Join(",", person.Features) : null,
                        DetectedAt = DateTime.UtcNow,
                        DetectionResultId = result.Id
                    });
                }

                await context.SaveChangesAsync(ct);
                _logger.LogDebug("💾 Saved {Count} persons for camera {Camera}", persons.Count, _cameraId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB batch save failed");
            }
        }

        /// <summary>
        /// OPTIMIZED annotation drawing - faster rendering
        /// </summary>
        private void DrawOptimizedAnnotations(Mat frame, List<TrackedPerson> persons)
        {
            const HersheyFonts font = HersheyFonts.HersheySimplex;
            const double fontScale = 0.45;
            const int thickness = 1;
            const int boxThickness = 2;
            const int cornerLen = 18;

            // Draw each person
            foreach (var person in persons)
            {
                // Get consistent color based on person ID
                var colorIndex = Math.Abs(person.GlobalPersonId.GetHashCode()) % _personColors.Length;
                var color = _personColors[colorIndex];
                var box = person.BoundingBox;

                // Draw corner box (faster than full rectangle)
                DrawCornerBoxFast(frame, box.X, box.Y, box.Width, box.Height, color, boxThickness, cornerLen);

                // Draw label
                var idStr = person.GlobalPersonId.ToString()[..6];
                var label = $"P-{idStr} {person.Confidence:P0}";

                var labelY = Math.Max(18, box.Y - 6);
                var labelPos = new CvPoint(box.X, labelY);

                // Background for text
                var textSize = Cv2.GetTextSize(label, font, fontScale, thickness, out _);
                Cv2.Rectangle(frame,
                    new Rect(labelPos.X - 1, labelPos.Y - textSize.Height - 2,
                            textSize.Width + 2, textSize.Height + 4),
                    color, -1);

                // Text
                Cv2.PutText(frame, label, labelPos, font, fontScale, _black, thickness, LineTypes.AntiAlias);
            }

            // Draw info overlay
            DrawInfoOverlay(frame, persons.Count);
        }

        private void DrawCornerBoxFast(Mat frame, int x, int y, int w, int h, Scalar color, int t, int len)
        {
            int x2 = x + w, y2 = y + h;
            len = Math.Min(len, Math.Min(w, h) / 3);

            // Top-left
            Cv2.Line(frame, new CvPoint(x, y), new CvPoint(x + len, y), color, t);
            Cv2.Line(frame, new CvPoint(x, y), new CvPoint(x, y + len), color, t);
            // Top-right
            Cv2.Line(frame, new CvPoint(x2, y), new CvPoint(x2 - len, y), color, t);
            Cv2.Line(frame, new CvPoint(x2, y), new CvPoint(x2, y + len), color, t);
            // Bottom-left
            Cv2.Line(frame, new CvPoint(x, y2), new CvPoint(x + len, y2), color, t);
            Cv2.Line(frame, new CvPoint(x, y2), new CvPoint(x, y2 - len), color, t);
            // Bottom-right
            Cv2.Line(frame, new CvPoint(x2, y2), new CvPoint(x2 - len, y2), color, t);
            Cv2.Line(frame, new CvPoint(x2, y2), new CvPoint(x2, y2 - len), color, t);
        }

        private void DrawInfoOverlay(Mat frame, int currentCount)
        {
            const HersheyFonts font = HersheyFonts.HersheySimplex;
            var todayUnique = _identityMatcher.GetTodayUniqueCount();

            // Background box
            Cv2.Rectangle(frame, new Rect(5, 5, 175, 62), _black, -1);

            // Line 1: Current
            Cv2.PutText(frame, $"Current: {currentCount}",
                new CvPoint(10, 20), font, 0.48, _yellow, 1);

            // Line 2: Unique Today (highlighted)
            Cv2.PutText(frame, $"Unique Today: {todayUnique}",
                new CvPoint(10, 40), font, 0.52, _green, 1);

            // Line 3: Time + FPS
            Cv2.PutText(frame, $"{DateTime.Now:HH:mm:ss} | {_currentFps:F0} FPS",
                new CvPoint(10, 58), font, 0.38, _white, 1);
        }

        private async Task NotifyClientsAsync(List<TrackedPerson> trackedPersons)
        {
            try
            {
                var todayUnique = _identityMatcher.GetTodayUniqueCount();

                var update = new
                {
                    cameraId = _cameraId,
                    count = trackedPersons.Count,
                    uniqueCount = _uniquePersonCount,
                    todayUniqueCount = todayUnique,
                    timestamp = DateTime.UtcNow,
                    fps = _currentFps,
                    persons = trackedPersons.Select(p => new
                    {
                        id = p.GlobalPersonId.ToString()[..8],
                        confidence = p.Confidence,
                        isNew = p.IsNew,
                        hasReId = p.HasReIdMatch
                    }).ToList()
                };

                await _hubContext.Clients.Group($"camera_{_cameraId}")
                    .SendAsync("DetectionUpdate", update);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify clients");
            }
        }

        public async IAsyncEnumerable<byte[]> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in _streamChannel.Reader.ReadAllAsync(ct))
                yield return frame;
        }

        public async IAsyncEnumerable<byte[]> GetAnnotatedFramesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in ReadFramesAsync(ct))
                yield return frame;
        }

        public void Disconnect()
        {
            if (!_isConnected && _capture == null) return;

            _logger.LogInformation("⏹️ Disconnecting camera {Id}...", _cameraId);
            _isConnected = false;

            try { _cts?.Cancel(); } catch { }

            try
            {
                var allTasks = new List<Task>();
                if (_captureTask != null) allTasks.Add(_captureTask);
                if (_streamTask != null) allTasks.Add(_streamTask);
                if (_detectionTask != null) allTasks.Add(_detectionTask);
                if (_saveTask != null) allTasks.Add(_saveTask);

                if (allTasks.Count > 0)
                {
                    Task.WaitAll(allTasks.ToArray(), TimeSpan.FromSeconds(5));
                }
            }
            catch { }

            try
            {
                _captureLock.Wait(TimeSpan.FromSeconds(2));
                try
                {
                    if (_capture != null)
                    {
                        _capture.Release();
                        _capture.Dispose();
                        _capture = null;
                    }
                }
                finally
                {
                    _captureLock.Release();
                }
            }
            catch { }

            // Clear stable tracks
            _stableTracks.Clear();

            _logger.LogInformation("✅ Camera {Id} disconnected", _cameraId);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();

            try { _cts?.Dispose(); } catch { }
            try { _captureLock.Dispose(); } catch { }
            try { _streamChannel.Writer.TryComplete(); } catch { }

            lock (_frameLock) { _currentJpeg = null; }
            lock (_detectionLock) { _currentTrackedPersons.Clear(); }

            _logger.LogInformation("✅ Camera {Id} processor disposed", _cameraId);
        }
    }
}