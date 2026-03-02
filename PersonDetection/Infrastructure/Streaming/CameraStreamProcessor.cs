// PersonDetection.Infrastructure/Streaming/CameraStreamProcessor.cs
namespace PersonDetection.Infrastructure.Streaming
{
    using Microsoft.AspNetCore.SignalR;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using OpenCvSharp;
    using PersonDetection.API.Hubs;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Services;
    using PersonDetection.Domain.ValueObjects;
    using PersonDetection.Infrastructure.Context;
    using PersonDetection.Infrastructure.Detection;
    using PersonDetection.Infrastructure.ReId;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Threading.Channels;
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
        private readonly StreamingSettings _streamingSettings;
        private readonly DetectionSettings _detectionSettings;
        private readonly PersistenceSettings _persistenceSettings;
        private readonly TrackingSettings _trackingSettings;
        private readonly IdentitySettings _identitySettings;
        private readonly ILogger<CameraStreamProcessor> _logger;

        private VideoCapture? _capture;
        private readonly SemaphoreSlim _captureLock = new(1, 1);
        private CancellationTokenSource? _cts;

        private Task? _captureTask;
        private Task? _streamTask;
        private Task? _detectionTask;
        private Task? _saveTask;

        private readonly Channel<byte[]> _streamChannel;

        private readonly object _frameLock = new();
        private byte[]? _currentJpeg;
        private int _frameWidth = 1280;
        private int _frameHeight = 720;

        private readonly object _detectionLock = new();
        private List<TrackedPerson> _currentTrackedPersons = new();
        private int _uniquePersonCount;
        private readonly HashSet<Guid> _seenPersonIds = new();

        private DateTime _lastDbSave = DateTime.MinValue;
        private int _lastSavedCount = -1;

        private int _frameCount;
        private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
        private double _currentFps;

        private volatile bool _disposed;
        private volatile bool _isConnected;

        private static readonly Scalar[] _personColors;
        private static readonly Scalar _yellow = new(0, 255, 255);
        private static readonly Scalar _white = new(255, 255, 255);
        private static readonly Scalar _green = new(0, 255, 0);
        private static readonly Scalar _black = new(0, 0, 0);

        private const int ThumbnailWidth = 64;
        private const int ThumbnailHeight = 128;
        private const int ThumbnailJpegQuality = 75;

        private volatile StreamConnectionState _connectionState = StreamConnectionState.Disconnected;
        private int _reconnectAttempt = 0;
        private DateTime _lastHealthNotification = DateTime.MinValue;
        public StreamConnectionState ConnectionState => _connectionState;

        static CameraStreamProcessor()
        {
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
            public bool HasReIdMatch { get; set; }
            public int TrackId { get; set; }
            public int StableFrameCount { get; set; }
        }

        private readonly Dictionary<int, StableTrack> _stableTracks = new();
        private int _nextTrackId = 1;

        private class StableTrack
        {
            public int TrackId { get; set; }
            public Guid GlobalPersonId { get; set; }
            public BoundingBox LastBox { get; set; } = null!;
            public BoundingBox? PreviousBox { get; set; }
            public DateTime LastSeen { get; set; }
            public int ConsecutiveFrames { get; set; }
            public float[]? Features { get; set; }
            public float LastConfidence { get; set; }
            public float MaxConfidence { get; set; }
            public bool IsStable { get; set; }
            public bool HasValidReId { get; set; }
            public float VelocityX { get; set; }
            public float VelocityY { get; set; }
            public bool IsFastWalker { get; set; }

            public (float X, float Y) PredictNextPosition()
            {
                var centerX = LastBox.X + LastBox.Width / 2f + VelocityX;
                var centerY = LastBox.Y + LastBox.Height / 2f + VelocityY;
                return (centerX, centerY);
            }
        }

        private class PendingPerson
        {
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public int FrameCount { get; set; }
            public bool HasFeatures { get; set; }
            public float MaxConfidence { get; set; }
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
            IOptions<TrackingSettings> trackingSettings,
            IOptions<IdentitySettings> identitySettings,
            ILogger<CameraStreamProcessor> logger)
        {
            _cameraId = cameraId;
            _detectionEngine = detectionEngine;
            _reidEngine = reidEngine;
            _identityMatcher = identityMatcher;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;
            _streamingSettings = streamingSettings.Value;
            _detectionSettings = detectionSettings.Value;
            _persistenceSettings = persistenceSettings.Value;
            _trackingSettings = trackingSettings.Value;
            _identitySettings = identitySettings.Value;
            _logger = logger;

            _streamChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });

            LogTrackingSettings();
        }

        private void LogTrackingSettings()
        {
            _logger.LogInformation(
                "📹 CameraStreamProcessor Settings:\n" +
                "   ├─ Spatial: NormalDist={Normal}px, FastDist={Fast}px, FeatureBonus={Bonus}px\n" +
                "   ├─ Stable Track: MinFrames={Frames}, MinConf={Conf:P0}\n" +
                "   ├─ Fast Walker: SpeedThreshold={Speed}px/f, StableFrames={FFrames}, StableConf={FConf:P0}\n" +
                "   ├─ Velocity: Alpha={Alpha:F2}\n" +
                "   ├─ Cleanup: NormalTimeout={NTimeout}s, FastTimeout={FTimeout}s\n" +
                "   └─ Confirmation: 2Frame={TwoFrame:P0}, HighConf={HighConf:P0}, VeryHigh={VeryHigh:P0}",
                _trackingSettings.NormalMaxDistance,
                _trackingSettings.FastWalkerMaxDistance,
                _trackingSettings.FeatureMatchBonusDistance,
                _trackingSettings.StableTrackMinFrames,
                _trackingSettings.StableTrackMinConfidence,
                _trackingSettings.FastWalkerSpeedThreshold,
                _trackingSettings.FastWalkerStableFrames,
                _trackingSettings.FastWalkerStableConfidence,
                _trackingSettings.VelocitySmoothingAlpha,
                _trackingSettings.NormalTrackTimeoutSeconds,
                _trackingSettings.FastWalkerTrackTimeoutSeconds,
                _trackingSettings.TwoFrameConfirmMinConfidence,
                _trackingSettings.HighConfSingleFrameThreshold,
                _identitySettings.VeryHighConfidenceThreshold);
        }

        #region Connection & Task Management

        public async Task<bool> ConnectAsync(string url, CancellationToken ct = default)
        {
            StreamUrl = url;
            _reconnectAttempt = 0;

            await UpdateConnectionStateAsync(StreamConnectionState.Connecting,
                "Initiating connection", ct);

            for (int attempt = 1; attempt <= _streamingSettings.MaxReconnectAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    _isConnected = await ConnectWithOpenCvAsync(url, ct);

                    if (_isConnected)
                    {
                        _identityMatcher.SetFrameDimensions(_frameWidth, _frameHeight);

                        await UpdateConnectionStateAsync(StreamConnectionState.Connected,
                            "Connected successfully", ct);

                        StartAllTasks();

                        _logger.LogInformation("✅ Camera {Id} connected to {Url} ({W}x{H})",
                            _cameraId, url, _frameWidth, _frameHeight);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Connection attempt {Attempt}/{Max} failed",
                        attempt, _streamingSettings.MaxReconnectAttempts);
                }

                if (attempt < _streamingSettings.MaxReconnectAttempts)
                {
                    var delay = CalculateReconnectDelay(attempt);
                    await Task.Delay(delay, ct);
                }
            }

            await UpdateConnectionStateAsync(StreamConnectionState.Error,
                "Failed to connect after all attempts", ct);

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
                _capture.Set(VideoCaptureProperties.FrameWidth, _streamingSettings.ResizeWidth);
                _capture.Set(VideoCaptureProperties.FrameHeight, _streamingSettings.ResizeHeight);

                _frameWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                _frameHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                var fps = _capture.Get(VideoCaptureProperties.Fps);

                if (_frameWidth <= 0) _frameWidth = _streamingSettings.ResizeWidth;
                if (_frameHeight <= 0) _frameHeight = _streamingSettings.ResizeHeight;

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

        #endregion

        #region Capture Loop

        private async Task SafeCaptureLoop(CancellationToken ct)
        {
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _streamingSettings.TargetFps);
            int consecutiveErrors = 0;

            _logger.LogInformation("🎥 Capture loop started for camera {Id}", _cameraId);

            while (!ct.IsCancellationRequested && !_disposed)
            {
                // Check if we need to reconnect
                if (!_isConnected)
                {
                    if (_streamingSettings.EnableAutoReconnect)
                    {
                        var reconnected = await TryReconnectAsync(ct);
                        if (!reconnected)
                        {
                            await UpdateConnectionStateAsync(StreamConnectionState.Error,
                                "Max reconnection attempts reached", ct);
                            break;
                        }
                        consecutiveErrors = 0;

                        // CRITICAL: Restart other loops after successful reconnection
                        RestartProcessingTasks(ct);

                        continue;
                    }
                    else
                    {
                        break;
                    }
                }

                try
                {
                    var startTime = DateTime.UtcNow;
                    byte[]? jpegData = null;

                    if (!await _captureLock.WaitAsync(_streamingSettings.ReadTimeoutMs, ct))
                    {
                        consecutiveErrors++;
                        await CheckHealthAndNotifyAsync(ct);
                        continue;
                    }

                    try
                    {
                        if (_capture == null || !_capture.IsOpened())
                        {
                            consecutiveErrors++;

                            if (consecutiveErrors > _streamingSettings.MaxConsecutiveFrameErrors)
                            {
                                _logger.LogWarning("⚠️ Camera {Id}: Max consecutive errors ({Max}) reached",
                                    _cameraId, _streamingSettings.MaxConsecutiveFrameErrors);

                                _isConnected = false;
                                await UpdateConnectionStateAsync(StreamConnectionState.Reconnecting,
                                    "Stream lost, attempting reconnection", ct);
                                continue;
                            }

                            await Task.Delay(100, ct);
                            continue;
                        }

                        using var frame = new Mat();
                        bool success = _capture.Read(frame);

                        if (!success || frame.Empty())
                        {
                            consecutiveErrors++;

                            if (consecutiveErrors > _streamingSettings.MaxConsecutiveFrameErrors)
                            {
                                _logger.LogWarning("⚠️ Camera {Id}: Cannot read frames, triggering reconnect", _cameraId);
                                _isConnected = false;
                                await UpdateConnectionStateAsync(StreamConnectionState.Reconnecting,
                                    "Frame read failed, reconnecting", ct);
                            }

                            await Task.Delay(50, ct);
                            continue;
                        }

                        consecutiveErrors = 0;

                        if (frame.Width != _frameWidth || frame.Height != _frameHeight)
                        {
                            _frameWidth = frame.Width;
                            _frameHeight = frame.Height;
                            _identityMatcher.SetFrameDimensions(_frameWidth, _frameHeight);
                        }

                        Cv2.ImEncode(".jpg", frame, out jpegData, new[]
                        {
                    (int)ImwriteFlags.JpegQuality, _streamingSettings.JpegQuality
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

                    await CheckHealthAndNotifyAsync(ct);

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

                    if (consecutiveErrors > _streamingSettings.MaxConsecutiveFrameErrors)
                    {
                        _isConnected = false;
                        await UpdateConnectionStateAsync(StreamConnectionState.Reconnecting,
                            $"Exception occurred: {ex.Message}", ct);
                    }

                    try { await Task.Delay(100, ct); } catch { break; }
                }
            }

            await UpdateConnectionStateAsync(StreamConnectionState.Stopped, "Capture loop ended", ct);
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

        #endregion

        #region Stream Output Loop

        private async Task OptimizedStreamOutputLoop(CancellationToken ct)
        {
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _streamingSettings.TargetFps);
            var encodeParams = new[] { (int)ImwriteFlags.JpegQuality, _streamingSettings.JpegQuality };

            _logger.LogDebug("📺 Stream output loop started for camera {Id}", _cameraId);

            while (!ct.IsCancellationRequested && !_disposed)
            {
                try
                {
                    // Wait during reconnection instead of exiting
                    if (!_isConnected)
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }

                    await Task.Delay(frameInterval, ct);

                    byte[]? jpeg;
                    lock (_frameLock) { jpeg = _currentJpeg; }
                    if (jpeg == null) continue;

                    List<TrackedPerson> persons;
                    lock (_detectionLock) { persons = _currentTrackedPersons.ToList(); }

                    using var frame = Mat.FromImageData(jpeg, ImreadModes.Color);
                    if (frame.Empty()) continue;

                    DrawOptimizedAnnotations(frame, persons);

                    Cv2.ImEncode(".jpg", frame, out var annotatedJpeg, encodeParams);
                    _streamChannel.Writer.TryWrite(annotatedJpeg);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stream output error");
                    await Task.Delay(100, ct);
                }
            }

            _logger.LogDebug("📺 Stream output loop ended for camera {Id}", _cameraId);
        }

        #endregion

        #region Detection Loop (Fully Configurable)

        private async Task OptimizedDetectionLoop(CancellationToken ct)
        {
            var detectionInterval = TimeSpan.FromMilliseconds(_streamingSettings.DetectionIntervalMs);
            var config = new YoloDetectionConfig
            {
                ConfidenceThreshold = _detectionSettings.ConfidenceThreshold,
                NmsThreshold = _detectionSettings.NmsThreshold,
                ModelInputSize = _detectionSettings.ModelInputSize
            };

            var reidConfig = new OSNetConfig();
            int framesSinceLastReId = 0;
            var reIdInterval = Math.Max(1, _streamingSettings.ReIdEveryNFrames);

            var pendingConfirmations = new Dictionary<Guid, PendingPerson>();

            while (!ct.IsCancellationRequested && !_disposed)
            {
                try
                {
                    // Wait during reconnection instead of exiting
                    if (!_isConnected)
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }

                    await Task.Delay(detectionInterval, ct);

                    byte[]? jpeg;
                    lock (_frameLock) { jpeg = _currentJpeg; }
                    if (jpeg == null) continue;

                    var detections = await _detectionEngine.DetectAsync(jpeg, config, ct);

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
                    var usedTrackIds = new HashSet<int>();
                    var now = DateTime.UtcNow;

                    CleanupStaleTracks(now);

                    // ═══════════════════════════════════════════════════════════════
                    // PHASE 1: Extract features first for better track matching
                    // ═══════════════════════════════════════════════════════════════
                    var detectionFeatures = new Dictionary<int, float[]>();

                    if (shouldRunReId)
                    {
                        for (int i = 0; i < detections.Count; i++)
                        {
                            var detection = detections[i];
                            try
                            {
                                var cropArea = detection.BoundingBox.Width * detection.BoundingBox.Height;
                                var minArea = _identitySettings.MinCropWidth * _identitySettings.MinCropHeight;

                                if (cropArea >= minArea * _identitySettings.MinCropAreaMultiplier)
                                {
                                    var featureVector = await _reidEngine!.ExtractFeaturesAsync(
                                        jpeg, detection.BoundingBox, reidConfig, ct);
                                    detectionFeatures[i] = featureVector.Values;
                                }
                            }
                            catch { /* ignore feature extraction failures */ }
                        }
                    }

                    // ═══════════════════════════════════════════════════════════════
                    // PHASE 2: Match detections to tracks
                    // ═══════════════════════════════════════════════════════════════
                    for (int i = 0; i < detections.Count; i++)
                    {
                        var detection = detections[i];
                        detectionFeatures.TryGetValue(i, out var features);

                        var tracked = new TrackedPerson
                        {
                            BoundingBox = detection.BoundingBox,
                            Confidence = detection.Confidence,
                            GlobalPersonId = Guid.Empty,
                            HasReIdMatch = false,
                            Features = features
                        };

                        // Find existing track (with feature-assisted matching)
                        var existingTrack = FindExistingTrack(detection.BoundingBox, features);

                        // Ensure track isn't already used this frame
                        if (existingTrack != null && usedTrackIds.Contains(existingTrack.TrackId))
                        {
                            existingTrack = null;
                        }

                        if (existingTrack != null)
                        {
                            tracked.TrackId = existingTrack.TrackId;
                            usedTrackIds.Add(existingTrack.TrackId);

                            existingTrack.MaxConfidence = Math.Max(existingTrack.MaxConfidence, detection.Confidence);
                            existingTrack.LastConfidence = detection.Confidence;

                            // Check if track is STABLE (using config)
                            bool isTrackStable = IsTrackStable(existingTrack);

                            // FAST WALKER BOOST: Also stable if fast + has features
                            if (!isTrackStable && existingTrack.IsFastWalker &&
                                existingTrack.HasValidReId && features != null &&
                                _identitySettings.EnableFastWalkerMode)
                            {
                                isTrackStable = true;
                                existingTrack.IsStable = true;
                            }

                            if (isTrackStable)
                            {
                                tracked.GlobalPersonId = existingTrack.GlobalPersonId;
                                tracked.HasReIdMatch = true;

                                _logger.LogDebug(
                                    "🔒 STABLE: #{Track} → {Id} (fast={Fast}, frames={F})",
                                    tracked.TrackId,
                                    tracked.GlobalPersonId.ToString()[..8],
                                    existingTrack.IsFastWalker,
                                    existingTrack.ConsecutiveFrames);
                            }
                            else
                            {
                                tracked.GlobalPersonId = existingTrack.GlobalPersonId;
                            }
                        }
                        else
                        {
                            tracked.TrackId = _nextTrackId++;
                            usedTrackIds.Add(tracked.TrackId);
                        }

                        // ═══════════════════════════════════════════════════════════
                        // PHASE 3: Run ReID for non-stable tracks
                        // ═══════════════════════════════════════════════════════════
                        bool trackIsStable = existingTrack?.IsStable ?? false;

                        if (shouldRunReId && !trackIsStable && features != null)
                        {
                            try
                            {
                                var featureVector = new FeatureVector(features);

                                var reidId = _identityMatcher.GetOrCreateIdentity(
                                    featureVector,
                                    _cameraId,
                                    detection.BoundingBox,
                                    detection.Confidence,
                                    tracked.TrackId);

                                if (reidId != Guid.Empty)
                                {
                                    if (tracked.GlobalPersonId == Guid.Empty ||
                                        (existingTrack != null && !existingTrack.HasValidReId) ||
                                        (existingTrack != null && existingTrack.ConsecutiveFrames < _trackingSettings.StableTrackMinFrames))
                                    {
                                        tracked.GlobalPersonId = reidId;
                                        tracked.HasReIdMatch = true;

                                        if (existingTrack != null)
                                        {
                                            existingTrack.HasValidReId = true;
                                        }

                                        _logger.LogDebug(
                                            "🔍 ReID: #{Track} → {Id} (conf={Conf:P0})",
                                            tracked.TrackId, reidId.ToString()[..8], detection.Confidence);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "ReID failed for track {Track}", tracked.TrackId);
                            }
                        }

                        // Ensure we have a GlobalPersonId
                        if (tracked.GlobalPersonId == Guid.Empty)
                        {
                            if (existingTrack != null && existingTrack.GlobalPersonId != Guid.Empty)
                            {
                                tracked.GlobalPersonId = existingTrack.GlobalPersonId;
                            }
                            else
                            {
                                tracked.GlobalPersonId = Guid.NewGuid();
                            }
                        }

                        UpdateStableTrack(tracked, now);

                        // ═══════════════════════════════════════════════════════════
                        // PHASE 4: Confirmation (with configurable thresholds)
                        // ═══════════════════════════════════════════════════════════
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
                            pending.HasFeatures |= (features != null);
                            pending.MaxConfidence = Math.Max(pending.MaxConfidence, detection.Confidence);

                            bool shouldConfirm = false;
                            string confirmReason = "";

                            var trackInfo = _stableTracks.GetValueOrDefault(tracked.TrackId);
                            bool isFastWalker = trackInfo?.IsFastWalker ?? false;

                            // FAST WALKER: Confirm on single frame with features (configurable)
                            if (isFastWalker && pending.HasFeatures && _identitySettings.EnableFastWalkerMode)
                            {
                                shouldConfirm = true;
                                confirmReason = "FAST+FEATURES";
                            }
                            // Normal: 2+ frames with decent confidence (configurable)
                            else if (pending.FrameCount >= _identitySettings.ConfirmationMatchCount &&
                                     pending.MaxConfidence >= _trackingSettings.TwoFrameConfirmMinConfidence)
                            {
                                shouldConfirm = true;
                                confirmReason = $"{_identitySettings.ConfirmationMatchCount}-FRAMES";
                            }
                            // High confidence single frame with features (configurable)
                            else if (pending.HasFeatures &&
                                     pending.MaxConfidence >= _trackingSettings.HighConfSingleFrameThreshold)
                            {
                                shouldConfirm = true;
                                confirmReason = "HIGH-CONF";
                            }
                            // Very high confidence (configurable)
                            else if (pending.MaxConfidence >= _identitySettings.VeryHighConfidenceThreshold)
                            {
                                shouldConfirm = true;
                                confirmReason = "VERY-HIGH-CONF";
                            }

                            if (shouldConfirm)
                            {
                                _seenPersonIds.Add(tracked.GlobalPersonId);
                                pendingConfirmations.Remove(tracked.GlobalPersonId);
                                tracked.IsNew = true;

                                _logger.LogInformation(
                                    "✅ CONFIRMED [{Reason}]: {Id} (frames={F}, conf={C:P0}, fast={Fast})",
                                    confirmReason,
                                    tracked.GlobalPersonId.ToString()[..8],
                                    pending.FrameCount,
                                    pending.MaxConfidence,
                                    isFastWalker);
                            }
                        }

                        seenThisFrame.Add(tracked.GlobalPersonId);
                        trackedPersons.Add(tracked);
                    }

                    if (shouldRunReId)
                    {
                        framesSinceLastReId = 0;

                        // Clean up stale pending confirmations (using config)
                        var staleIds = pendingConfirmations
                            .Where(kvp => !seenThisFrame.Contains(kvp.Key) ||
                                          kvp.Value.FrameCount > _trackingSettings.MaxPendingConfirmationAge ||
                                          (now - kvp.Value.LastSeen).TotalSeconds > _trackingSettings.MaxPendingConfirmationSeconds)
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

        #endregion

        #region Stable Track Management (Fully Configurable)

        /// <summary>
        /// Check if track is stable using configurable thresholds
        /// </summary>
        private bool IsTrackStable(StableTrack track)
        {
            if (track.IsFastWalker && _identitySettings.EnableFastWalkerMode)
            {
                // Fast walkers use different thresholds
                return track.ConsecutiveFrames >= _trackingSettings.FastWalkerStableFrames &&
                       track.MaxConfidence >= _trackingSettings.FastWalkerStableConfidence &&
                       track.HasValidReId;
            }
            else
            {
                // Normal walkers
                return track.ConsecutiveFrames >= _trackingSettings.StableTrackMinFrames &&
                       track.MaxConfidence >= _trackingSettings.StableTrackMinConfidence &&
                       track.HasValidReId;
            }
        }

        /// <summary>
        /// Find existing track with velocity prediction (configurable)
        /// </summary>
        private StableTrack? FindExistingTrack(BoundingBox newBox, float[]? newFeatures = null)
        {
            StableTrack? best = null;
            float bestScore = float.MaxValue;

            var newCenterX = newBox.X + newBox.Width / 2f;
            var newCenterY = newBox.Y + newBox.Height / 2f;

            foreach (var track in _stableTracks.Values)
            {
                // Calculate distance to track's LAST position
                var lastCenterX = track.LastBox.X + track.LastBox.Width / 2f;
                var lastCenterY = track.LastBox.Y + track.LastBox.Height / 2f;

                var dxLast = newCenterX - lastCenterX;
                var dyLast = newCenterY - lastCenterY;
                var distToLast = (float)Math.Sqrt(dxLast * dxLast + dyLast * dyLast);

                // Calculate distance to PREDICTED position (using velocity)
                var (predX, predY) = track.PredictNextPosition();
                var dxPred = newCenterX - predX;
                var dyPred = newCenterY - predY;
                var distToPredicted = (float)Math.Sqrt(dxPred * dxPred + dyPred * dyPred);

                // Use the SMALLER of the two distances
                var centerDist = Math.Min(distToLast, distToPredicted);

                // Determine max distance based on track's speed (configurable)
                float maxDistance = track.IsFastWalker
                    ? _trackingSettings.FastWalkerMaxDistance
                    : _trackingSettings.NormalMaxDistance;

                // BONUS: If we have features and they match well, allow larger distance
                float featureBonus = 0f;
                if (newFeatures != null && track.Features != null)
                {
                    var featureDist = CalculateFeatureDistance(newFeatures, track.Features);
                    if (featureDist < _trackingSettings.FeatureMatchThreshold)
                    {
                        featureBonus = _trackingSettings.FeatureMatchBonusDistance;
                        maxDistance += featureBonus;
                    }
                }

                if (centerDist > maxDistance) continue;

                // Size similarity bonus
                var sizeRatio = Math.Min(
                    (float)newBox.Width / track.LastBox.Width,
                    (float)track.LastBox.Width / newBox.Width) *
                    Math.Min(
                    (float)newBox.Height / track.LastBox.Height,
                    (float)track.LastBox.Height / newBox.Height);

                var score = centerDist / Math.Max(sizeRatio, 0.5f);

                // Bonus for predicted position match (configurable)
                if (distToPredicted < distToLast)
                {
                    score *= _trackingSettings.PredictionMatchBonus;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = track;
                }
            }

            return best;
        }

        /// <summary>
        /// Calculate Euclidean distance between feature vectors
        /// </summary>
        private float CalculateFeatureDistance(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return float.MaxValue;

            float sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                var diff = a[i] - b[i];
                sum += diff * diff;
            }
            return (float)Math.Sqrt(sum);
        }

        /// <summary>
        /// Update stable track with velocity calculation (configurable)
        /// </summary>
        private void UpdateStableTrack(TrackedPerson person, DateTime now)
        {
            if (!_stableTracks.TryGetValue(person.TrackId, out var track))
            {
                track = new StableTrack
                {
                    TrackId = person.TrackId,
                    MaxConfidence = 0,
                    IsStable = false,
                    HasValidReId = false,
                    VelocityX = 0,
                    VelocityY = 0,
                    IsFastWalker = false
                };
                _stableTracks[person.TrackId] = track;
            }

            // Calculate velocity for next frame prediction
            if (track.LastBox != null && track.ConsecutiveFrames > 0)
            {
                var oldCenterX = track.LastBox.X + track.LastBox.Width / 2f;
                var oldCenterY = track.LastBox.Y + track.LastBox.Height / 2f;
                var newCenterX = person.BoundingBox.X + person.BoundingBox.Width / 2f;
                var newCenterY = person.BoundingBox.Y + person.BoundingBox.Height / 2f;

                var dx = newCenterX - oldCenterX;
                var dy = newCenterY - oldCenterY;

                // Smooth velocity with configurable alpha
                var alpha = _trackingSettings.VelocitySmoothingAlpha;
                track.VelocityX = alpha * dx + (1 - alpha) * track.VelocityX;
                track.VelocityY = alpha * dy + (1 - alpha) * track.VelocityY;

                // Detect fast walker (configurable threshold)
                var speed = (float)Math.Sqrt(track.VelocityX * track.VelocityX +
                                              track.VelocityY * track.VelocityY);
                track.IsFastWalker = speed > _trackingSettings.FastWalkerSpeedThreshold;

                track.PreviousBox = track.LastBox;
            }

            track.GlobalPersonId = person.GlobalPersonId;
            track.LastBox = person.BoundingBox;
            track.LastSeen = now;
            track.ConsecutiveFrames++;
            track.Features = person.Features ?? track.Features;
            track.LastConfidence = person.Confidence;
            track.MaxConfidence = Math.Max(track.MaxConfidence, person.Confidence);

            if (person.Features != null && person.HasReIdMatch)
            {
                track.HasValidReId = true;
            }

            // Update stability using configurable check
            track.IsStable = IsTrackStable(track);
        }

        /// <summary>
        /// Cleanup stale tracks (configurable timeouts)
        /// </summary>
        private void CleanupStaleTracks(DateTime now)
        {
            var staleTrackIds = _stableTracks
                .Where(kvp =>
                {
                    var age = (now - kvp.Value.LastSeen).TotalSeconds;
                    // Use configurable timeouts
                    var maxAge = kvp.Value.IsFastWalker
                        ? _trackingSettings.FastWalkerTrackTimeoutSeconds
                        : _trackingSettings.NormalTrackTimeoutSeconds;
                    return age > maxAge;
                })
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in staleTrackIds)
            {
                _stableTracks.Remove(id);
            }
        }

        #endregion

        #region Reconnection & Health

        private async Task<bool> TryReconnectAsync(CancellationToken ct)
        {
            var maxAttempts = _streamingSettings.MaxReconnectAttempts;

            while ((maxAttempts == 0 || _reconnectAttempt < maxAttempts) && !ct.IsCancellationRequested)
            {
                _reconnectAttempt++;

                var delay = CalculateReconnectDelay(_reconnectAttempt);

                _logger.LogInformation(
                    "🔄 Camera {Id}: Reconnection attempt {Attempt}/{Max} in {Delay}ms",
                    _cameraId, _reconnectAttempt,
                    maxAttempts == 0 ? "∞" : maxAttempts.ToString(),
                    delay);

                await UpdateConnectionStateAsync(StreamConnectionState.Reconnecting,
                    $"Reconnecting (attempt {_reconnectAttempt})", ct);

                await Task.Delay(delay, ct);

                try
                {
                    await CleanupCaptureAsync();

                    _isConnected = await ConnectWithOpenCvAsync(StreamUrl, ct);

                    if (_isConnected)
                    {
                        _reconnectAttempt = 0;

                        await UpdateConnectionStateAsync(StreamConnectionState.Connected,
                            "Reconnected successfully", ct);

                        _logger.LogInformation(
                            "✅ Camera {Id}: Reconnected successfully",
                            _cameraId);

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Camera {Id}: Reconnection attempt {Attempt} failed",
                        _cameraId, _reconnectAttempt);
                }
            }

            _logger.LogError("❌ Camera {Id}: All reconnection attempts exhausted", _cameraId);
            return false;
        }

        private int CalculateReconnectDelay(int attempt)
        {
            var baseDelay = _streamingSettings.InitialReconnectDelayMs;
            var multiplier = _streamingSettings.ReconnectBackoffMultiplier;
            var maxDelay = _streamingSettings.MaxReconnectDelayMs;

            var delay = (int)(baseDelay * Math.Pow(multiplier, attempt - 1));

            return Math.Min(delay, maxDelay);
        }

        private async Task CleanupCaptureAsync()
        {
            await _captureLock.WaitAsync();
            try
            {
                if (_capture != null)
                {
                    try
                    {
                        _capture.Release();
                        _capture.Dispose();
                    }
                    catch { }
                    _capture = null;
                }
            }
            finally
            {
                _captureLock.Release();
            }
        }

        private async Task UpdateConnectionStateAsync(
            StreamConnectionState newState,
            string message,
            CancellationToken ct)
        {
            var previousState = _connectionState;
            _connectionState = newState;

            if (!_streamingSettings.NotifyClientsOnStatusChange)
                return;

            if (previousState == newState &&
                (DateTime.UtcNow - _lastHealthNotification).TotalMilliseconds < _streamingSettings.StreamHealthCheckIntervalMs)
                return;

            try
            {
                var status = new
                {
                    cameraId = _cameraId,
                    state = (int)newState,
                    stateName = newState.ToString(),
                    stateMessage = message,
                    reconnectAttempt = _reconnectAttempt,
                    maxReconnectAttempts = _streamingSettings.MaxReconnectAttempts,
                    timestamp = DateTime.UtcNow,
                    fps = _currentFps,
                    consecutiveErrors = 0
                };

                await _hubContext.Clients.Group($"camera_{_cameraId}")
                    .SendAsync("StreamStatusUpdate", status, ct);

                await _hubContext.Clients.Group("stream_status")
                    .SendAsync("StreamStatusUpdate", status, ct);

                _lastHealthNotification = DateTime.UtcNow;

                _logger.LogDebug("📡 Camera {Id}: Status → {State} ({Message})",
                    _cameraId, newState, message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify stream status change");
            }
        }

        private async Task CheckHealthAndNotifyAsync(CancellationToken ct)
        {
            if (!_streamingSettings.NotifyClientsOnStatusChange)
                return;

            var timeSinceLastNotify = (DateTime.UtcNow - _lastHealthNotification).TotalMilliseconds;

            if (timeSinceLastNotify >= _streamingSettings.StreamHealthCheckIntervalMs)
            {
                await UpdateConnectionStateAsync(_connectionState,
                    _isConnected ? "Stream healthy" : "Stream unhealthy", ct);
            }
        }

        /// <summary>
        /// Restart processing tasks after successful reconnection
        /// </summary>
        private void RestartProcessingTasks(CancellationToken ct)
        {
            _logger.LogInformation("🔄 Camera {Id}: Restarting processing tasks after reconnection", _cameraId);

            // Check and restart stream output task
            if (_streamTask == null || _streamTask.IsCompleted)
            {
                _streamTask = Task.Run(() => OptimizedStreamOutputLoop(ct), ct);
                _logger.LogDebug("Camera {Id}: Stream output task restarted", _cameraId);
            }

            // Check and restart detection task
            if (_detectionTask == null || _detectionTask.IsCompleted)
            {
                _detectionTask = Task.Run(() => OptimizedDetectionLoop(ct), ct);
                _logger.LogDebug("Camera {Id}: Detection task restarted", _cameraId);
            }

            // Check and restart save task
            if (_saveTask == null || _saveTask.IsCompleted)
            {
                _saveTask = Task.Run(() => BatchDatabaseSaveLoop(ct), ct);
                _logger.LogDebug("Camera {Id}: Database save task restarted", _cameraId);
            }
        }

        #endregion

        #region Database Save Loop

        private async Task BatchDatabaseSaveLoop(CancellationToken ct)
        {
            var saveInterval = TimeSpan.FromSeconds(_persistenceSettings.SaveIntervalSeconds);
            var personsToSave = new List<TrackedPerson>();

            _logger.LogDebug("💾 Database save loop started for camera {Id}", _cameraId);

            while (!ct.IsCancellationRequested && !_disposed)  // ✅ CHANGED: removed _isConnected
            {
                try
                {
                    await Task.Delay(saveInterval, ct);

                    // Skip saving during reconnection
                    if (!_isConnected) continue;  // ✅ ADD THIS

                    if (!_persistenceSettings.SaveToDatabase) continue;

                    List<TrackedPerson> persons;
                    lock (_detectionLock) { persons = _currentTrackedPersons.ToList(); }

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

            _logger.LogDebug("💾 Database save loop ended for camera {Id}", _cameraId);
        }

        private async Task BatchSaveToDatabaseAsync(List<TrackedPerson> persons, CancellationToken ct)
        {
            if (persons.Count == 0) return;

            // ═══════════════════════════════════════════════════════
            // Pre-compute thumbnails OUTSIDE retry block
            // CPU work only — no DB dependency, no need to retry
            // ═══════════════════════════════════════════════════════
            byte[]? currentFrame;
            lock (_frameLock) { currentFrame = _currentJpeg; }

            var thumbnails = new Dictionary<Guid, byte[]>();
            if (currentFrame != null)
            {
                foreach (var person in persons)
                {
                    // One thumbnail per GlobalPersonId (skip duplicates in same batch)
                    if (!thumbnails.ContainsKey(person.GlobalPersonId))
                    {
                        var thumb = GenerateThumbnail(currentFrame, person.BoundingBox);
                        if (thumb != null)
                        {
                            thumbnails[person.GlobalPersonId] = thumb;
                        }
                    }
                }
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                var strategy = context.Database.CreateExecutionStrategy();
                var newPersonMappings = new List<(Guid GlobalId, int DbId)>();

                await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    newPersonMappings.Clear();

                    using var transaction = await context.Database.BeginTransactionAsync(ct);

                    try
                    {
                        // ═══════════════════════════════════════════
                        // Phase 1: Save DetectionResult → get result.Id
                        // ═══════════════════════════════════════════
                        var result = new DetectionResult
                        {
                            CameraId = _cameraId,
                            Timestamp = DateTime.UtcNow,
                            TotalDetections = persons.Count,
                            ValidDetections = persons.Count(p =>
                                p.Confidence >= _detectionSettings.ConfidenceThreshold),
                            UniquePersonCount = _uniquePersonCount
                        };
                        context.DetectionResults.Add(result);
                        await context.SaveChangesAsync(ct);

                        // ═══════════════════════════════════════════
                        // Phase 2: Batch-fetch ALL existing UniquePersons
                        // ═══════════════════════════════════════════
                        var globalIds = persons
                            .Select(p => p.GlobalPersonId)
                            .Distinct()
                            .ToList();

                        var existingPersons = await context.UniquePersons
                            .Where(u => globalIds.Contains(u.GlobalPersonId))
                            .ToDictionaryAsync(u => u.GlobalPersonId, ct);

                        // ═══════════════════════════════════════════
                        // Phase 3: Create new + update existing
                        //          Save ONCE to get all new IDs
                        // ═══════════════════════════════════════════
                        var newPersonEntries = new List<(TrackedPerson tracked, UniquePerson entity)>();

                        foreach (var person in persons)
                        {
                            // Try to get pre-computed thumbnail for this person
                            thumbnails.TryGetValue(person.GlobalPersonId, out var thumbnail);

                            if (!existingPersons.ContainsKey(person.GlobalPersonId))
                            {
                                // NEW person — create with thumbnail
                                var newUnique = UniquePerson.Create(
                                    person.GlobalPersonId,
                                    _cameraId,
                                    person.Features,
                                    thumbnail);              // ← Thumbnail passed here

                                context.UniquePersons.Add(newUnique);
                                existingPersons[person.GlobalPersonId] = newUnique;
                                newPersonEntries.Add((person, newUnique));
                            }
                            else
                            {
                                // EXISTING person — update, fill thumbnail if NULL
                                existingPersons[person.GlobalPersonId]
                                    .UpdateLastSeen(
                                        _cameraId,
                                        person.Features,
                                        thumbnail);           // ← Thumbnail passed here
                            }
                        }

                        if (newPersonEntries.Count > 0)
                        {
                            await context.SaveChangesAsync(ct);
                        }
                        // All UniquePersons now have valid .Id values

                        // ═══════════════════════════════════════════
                        // Phase 4: Add PersonSightings + DetectedPersons
                        // ═══════════════════════════════════════════
                        foreach (var person in persons)
                        {
                            var uniquePerson = existingPersons[person.GlobalPersonId];

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
                                FeatureVector = person.Features != null
                                    ? string.Join(",", person.Features)
                                    : null,
                                TrackId = person.TrackId,     // ← TrackId fix
                                DetectedAt = DateTime.UtcNow,
                                DetectionResultId = result.Id
                            });
                        }

                        await context.SaveChangesAsync(ct);

                        foreach (var (tracked, entity) in newPersonEntries)
                        {
                            newPersonMappings.Add((tracked.GlobalPersonId, entity.Id));
                        }

                        await transaction.CommitAsync(ct);
                    }
                    catch
                    {
                        try { await transaction.RollbackAsync(CancellationToken.None); }
                        catch { }
                        throw;
                    }
                });

                foreach (var (globalId, dbId) in newPersonMappings)
                {
                    _identityMatcher.SetDbId(globalId, dbId);
                }

                _logger.LogDebug("💾 Saved {Count} persons for camera {Camera}",
                    persons.Count, _cameraId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB batch save failed for camera {Camera}", _cameraId);
            }
        }
        /// <summary>
        /// Crop person from frame and generate a small JPEG thumbnail
        /// </summary>
        private byte[]? GenerateThumbnail(byte[] jpegData, BoundingBox box)
        {
            try
            {
                using var frame = Mat.FromImageData(jpegData, ImreadModes.Color);
                if (frame.Empty()) return null;

                // Clamp bounding box to frame boundaries
                var x = Math.Max(0, box.X);
                var y = Math.Max(0, box.Y);
                var w = Math.Min(box.Width, frame.Width - x);
                var h = Math.Min(box.Height, frame.Height - y);

                if (w <= 0 || h <= 0) return null;

                using var cropped = new Mat(frame, new Rect(x, y, w, h));
                using var resized = new Mat();
                Cv2.Resize(cropped, resized, new CvSize(ThumbnailWidth, ThumbnailHeight));

                Cv2.ImEncode(".jpg", resized, out var thumbnailBytes,
                    new[] { (int)ImwriteFlags.JpegQuality, ThumbnailJpegQuality });

                return thumbnailBytes;
            }
            catch
            {
                return null;
            }
        }
        #endregion

        #region Drawing & Notifications

        private void DrawOptimizedAnnotations(Mat frame, List<TrackedPerson> persons)
        {
            const HersheyFonts font = HersheyFonts.HersheySimplex;
            var fontScale = _streamingSettings.OverlayFontScale;
            var thickness = _streamingSettings.OverlayThickness;
            const int cornerLen = 18;

            foreach (var person in persons)
            {
                var colorIndex = Math.Abs(person.GlobalPersonId.GetHashCode()) % _personColors.Length;
                var color = _personColors[colorIndex];
                var box = person.BoundingBox;

                DrawCornerBoxFast(frame, box.X, box.Y, box.Width, box.Height, color, thickness, cornerLen);

                var idStr = person.GlobalPersonId.ToString()[..6];
                var label = _streamingSettings.ShowConfidence
                    ? $"P-{idStr} {person.Confidence:P0}"
                    : $"P-{idStr}";

                var labelY = Math.Max(18, box.Y - 6);
                var labelPos = new CvPoint(box.X, labelY);

                var textSize = Cv2.GetTextSize(label, font, fontScale, 1, out _);
                Cv2.Rectangle(frame,
                    new Rect(labelPos.X - 1, labelPos.Y - textSize.Height - 2,
                            textSize.Width + 2, textSize.Height + 4),
                    color, -1);

                Cv2.PutText(frame, label, labelPos, font, fontScale, _black, 1, LineTypes.AntiAlias);
            }

            DrawInfoOverlay(frame, persons.Count);
        }

        private void DrawCornerBoxFast(Mat frame, int x, int y, int w, int h, Scalar color, int t, int len)
        {
            int x2 = x + w, y2 = y + h;
            len = Math.Min(len, Math.Min(w, h) / 3);

            Cv2.Line(frame, new CvPoint(x, y), new CvPoint(x + len, y), color, t);
            Cv2.Line(frame, new CvPoint(x, y), new CvPoint(x, y + len), color, t);
            Cv2.Line(frame, new CvPoint(x2, y), new CvPoint(x2 - len, y), color, t);
            Cv2.Line(frame, new CvPoint(x2, y), new CvPoint(x2, y + len), color, t);
            Cv2.Line(frame, new CvPoint(x, y2), new CvPoint(x + len, y2), color, t);
            Cv2.Line(frame, new CvPoint(x, y2), new CvPoint(x, y2 - len), color, t);
            Cv2.Line(frame, new CvPoint(x2, y2), new CvPoint(x2 - len, y2), color, t);
            Cv2.Line(frame, new CvPoint(x2, y2), new CvPoint(x2, y2 - len), color, t);
        }

        private void DrawInfoOverlay(Mat frame, int currentCount)
        {
            const HersheyFonts font = HersheyFonts.HersheySimplex;
            var todayUnique = _identityMatcher.GetTodayUniqueCount();

            Cv2.Rectangle(frame, new Rect(5, 5, 175, 62), _black, -1);

            Cv2.PutText(frame, $"Current: {currentCount}",
                new CvPoint(10, 20), font, 0.48, _yellow, 1);

            Cv2.PutText(frame, $"Unique Today: {todayUnique}",
                new CvPoint(10, 40), font, 0.52, _green, 1);

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

        #endregion

        #region Public Methods

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
            _connectionState = StreamConnectionState.Stopped;

            _ = UpdateConnectionStateAsync(StreamConnectionState.Stopped,
                "Manually disconnected", CancellationToken.None);

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

            _stableTracks.Clear();
            _reconnectAttempt = 0;

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

        #endregion
    }
}
