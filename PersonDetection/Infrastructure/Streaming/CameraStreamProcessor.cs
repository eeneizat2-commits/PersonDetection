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
                        StartAllTasks();
                        _logger.LogInformation("✅ Camera {Id} connected to {Url}", _cameraId, url);
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

        /// <summary>
        /// Connect using OpenCV with FFMPEG for all stream types
        /// </summary>
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

                    // Set FFMPEG environment for low latency
                    Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS",
                        "rtsp_transport;tcp|buffer_size;1024000");

                    _capture = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);

                    if (_capture.IsOpened())
                    {
                        ConfigureCapture();
                        _logger.LogInformation("Stream opened via FFMPEG");
                        return true;
                    }

                    // Fallback to ANY
                    _capture.Dispose();
                    _capture = new VideoCapture(url, VideoCaptureAPIs.ANY);

                    if (_capture.IsOpened())
                    {
                        ConfigureCapture();
                        _logger.LogInformation("Stream opened via ANY backend");
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

                var width = _capture.Get(VideoCaptureProperties.FrameWidth);
                var height = _capture.Get(VideoCaptureProperties.FrameHeight);
                var fps = _capture.Get(VideoCaptureProperties.Fps);

                _logger.LogInformation("Camera {Id}: {Width}x{Height} @ {Fps} FPS",
                    _cameraId, width, height, fps);
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
            _streamTask = Task.Run(() => StreamOutputLoop(ct), ct);
            _detectionTask = Task.Run(() => DetectionLoop(ct), ct);
            _saveTask = Task.Run(() => DatabaseSaveLoop(ct), ct);

            _logger.LogInformation("All tasks started for camera {Id}", _cameraId);
        }

        /// <summary>
        /// THREAD-SAFE capture loop with proper error handling
        /// </summary>
        private async Task SafeCaptureLoop(CancellationToken ct)
        {
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _settings.TargetFps);
            int consecutiveErrors = 0;
            const int MaxConsecutiveErrors = 30;

            _logger.LogInformation("🎥 Capture loop started for camera {Id}", _cameraId);

            while (!ct.IsCancellationRequested && !_disposed)  // Simplified condition
            {
                // Check connection status inside the loop
                if (!_isConnected)
                {
                    _logger.LogDebug("Camera {Id} not connected, exiting capture loop", _cameraId);
                    break;
                }

                try
                {
                    var startTime = DateTime.UtcNow;
                    byte[]? jpegData = null;

                    // Try to acquire lock with timeout
                    if (!await _captureLock.WaitAsync(1000, ct))
                    {
                        _logger.LogWarning("Capture lock timeout for camera {Id}", _cameraId);
                        continue;
                    }

                    try
                    {
                        if (_capture == null || !_capture.IsOpened())
                        {
                            consecutiveErrors++;
                            if (consecutiveErrors > MaxConsecutiveErrors)
                            {
                                _logger.LogError("Too many capture errors, disconnecting camera {Id}", _cameraId);
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
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Capture loop cancelled for camera {Id}", _cameraId);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogDebug("Capture disposed for camera {Id}", _cameraId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Capture error for camera {Id}", _cameraId);
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

        private async Task StreamOutputLoop(CancellationToken ct)
        {
            var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _settings.TargetFps);

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

                    // Decode, annotate, encode
                    using var frame = Mat.FromImageData(jpeg, ImreadModes.Color);
                    if (frame.Empty()) continue;

                    DrawAnnotations(frame, persons);

                    Cv2.ImEncode(".jpg", frame, out var annotatedJpeg, new[]
                    {
                        (int)ImwriteFlags.JpegQuality, _settings.JpegQuality
                    });

                    _streamChannel.Writer.TryWrite(annotatedJpeg);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stream output error");
                }
            }
        }

        private async Task DetectionLoop(CancellationToken ct)
        {
            var detectionInterval = TimeSpan.FromMilliseconds(_settings.DetectionIntervalMs);
            var config = new YoloDetectionConfig
            {
                ConfidenceThreshold = _detectionSettings.ConfidenceThreshold,
                NmsThreshold = _detectionSettings.NmsThreshold,
                ModelInputSize = _detectionSettings.ModelInputSize
            };

            int framesSinceLastReId = 0;
            var reIdInterval = _settings.ReIdEveryNFrames > 0 ? _settings.ReIdEveryNFrames : 2;

            // Multi-frame confirmation tracking
            var pendingConfirmations = new Dictionary<Guid, PendingPerson>();
            const int RequiredConfirmationFrames = 3;
            const int MaxPendingAge = 10;

            // Spatial tracking for non-ReId frames
            var spatialTracker = new Dictionary<Guid, TrackedPerson>();

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

                    foreach (var detection in detections)
                    {
                        var tracked = new TrackedPerson
                        {
                            BoundingBox = detection.BoundingBox,
                            Confidence = detection.Confidence,
                            GlobalPersonId = Guid.Empty
                        };

                        Guid resolvedId = Guid.Empty;

                        // Strategy 1: Try spatial tracking first (fast, for continuity)
                        var spatialMatch = FindBestSpatialMatch(detection.BoundingBox, spatialTracker);
                        if (spatialMatch != null)
                        {
                            resolvedId = spatialMatch.GlobalPersonId;
                            tracked.GlobalPersonId = resolvedId;
                            tracked.Features = spatialMatch.Features;

                            _logger.LogDebug("📍 Spatial match: {Id}", resolvedId.ToString()[..8]);
                        }

                        // Strategy 2: Run Re-ID for identity verification/creation
                        if (shouldRunReId)
                        {
                            try
                            {
                                var reidConfig = new OSNetConfig();
                                var featureVector = await _reidEngine!.ExtractFeaturesAsync(
                                    jpeg, detection.BoundingBox, reidConfig, ct);

                                tracked.Features = featureVector.Values;

                                // Get or create identity with spatial context
                                var reidId = _identityMatcher.GetOrCreateIdentity(
                                    featureVector, _cameraId, detection.BoundingBox);

                                // If Re-ID gives different result than spatial, trust Re-ID
                                if (resolvedId == Guid.Empty || resolvedId != reidId)
                                {
                                    if (resolvedId != Guid.Empty && resolvedId != reidId)
                                    {
                                        _logger.LogDebug("🔄 Re-ID override: {Old} → {New}",
                                            resolvedId.ToString()[..8], reidId.ToString()[..8]);
                                    }
                                    resolvedId = reidId;
                                    tracked.GlobalPersonId = reidId;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Re-ID failed for detection");
                            }
                        }

                        // Strategy 3: Position-based fallback
                        if (resolvedId == Guid.Empty && _currentTrackedPersons.Count > 0)
                        {
                            var bestMatch = FindBestPositionMatch(detection.BoundingBox);
                            if (bestMatch != null)
                            {
                                tracked.GlobalPersonId = bestMatch.GlobalPersonId;
                                tracked.Features = bestMatch.Features;
                                resolvedId = bestMatch.GlobalPersonId;

                                _logger.LogDebug("📐 IoU match: {Id}", resolvedId.ToString()[..8]);
                            }
                        }

                        // Create temporary tracking ID if still unresolved
                        if (tracked.GlobalPersonId == Guid.Empty)
                        {
                            tracked.GlobalPersonId = Guid.NewGuid();
                            _logger.LogDebug("🔄 Temp ID: {Id}", tracked.GlobalPersonId.ToString()[..8]);
                        }

                        resolvedId = tracked.GlobalPersonId;

                        // Confirmation logic for unique count
                        if (!_seenPersonIds.Contains(resolvedId))
                        {
                            if (!pendingConfirmations.ContainsKey(resolvedId))
                            {
                                pendingConfirmations[resolvedId] = new PendingPerson
                                {
                                    FirstSeen = DateTime.UtcNow,
                                    FrameCount = 0,
                                    HasFeatures = tracked.Features != null
                                };
                            }

                            var pending = pendingConfirmations[resolvedId];
                            pending.FrameCount++;
                            pending.LastSeen = DateTime.UtcNow;

                            // Only confirm if we have features AND enough frames
                            if (pending.FrameCount >= RequiredConfirmationFrames && pending.HasFeatures)
                            {
                                _seenPersonIds.Add(resolvedId);
                                pendingConfirmations.Remove(resolvedId);
                                tracked.IsNew = true;

                                _logger.LogInformation("✅ CONFIRMED: {Id} after {Frames} frames",
                                    resolvedId.ToString()[..8], RequiredConfirmationFrames);
                            }
                        }

                        seenThisFrame.Add(resolvedId);
                        trackedPersons.Add(tracked);
                    }

                    // Update spatial tracker
                    spatialTracker.Clear();
                    foreach (var p in trackedPersons)
                    {
                        spatialTracker[p.GlobalPersonId] = p;
                    }

                    if (shouldRunReId)
                    {
                        framesSinceLastReId = 0;

                        // Clean up stale pending confirmations
                        var staleIds = pendingConfirmations
                            .Where(kvp => !seenThisFrame.Contains(kvp.Key) ||
                                          kvp.Value.FrameCount > MaxPendingAge)
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
                        _uniquePersonCount = _seenPersonIds.Count;
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

        private class PendingPerson
        {
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public int FrameCount { get; set; }
            public bool HasFeatures { get; set; }
        }

        private TrackedPerson? FindBestSpatialMatch(BoundingBox newBox, Dictionary<Guid, TrackedPerson> tracker)
        {
            TrackedPerson? best = null;
            float bestScore = float.MaxValue;

            foreach (var (id, person) in tracker)
            {
                // Calculate center distance
                var dx = (newBox.X + newBox.Width / 2f) - (person.BoundingBox.X + person.BoundingBox.Width / 2f);
                var dy = (newBox.Y + newBox.Height / 2f) - (person.BoundingBox.Y + person.BoundingBox.Height / 2f);
                var centerDist = (float)Math.Sqrt(dx * dx + dy * dy);

                // Also consider size similarity
                var sizeRatio = Math.Min(
                    (float)newBox.Width / person.BoundingBox.Width,
                    (float)person.BoundingBox.Width / newBox.Width) *
                    Math.Min(
                    (float)newBox.Height / person.BoundingBox.Height,
                    (float)person.BoundingBox.Height / newBox.Height);

                // Combined score (lower is better)
                var score = centerDist / sizeRatio;

                if (score < bestScore && centerDist < 100) // Max 100px movement
                {
                    bestScore = score;
                    best = person;
                }
            }

            return best;
        }

        private TrackedPerson? FindBestPositionMatch(BoundingBox newBox)
        {
            TrackedPerson? best = null;
            float bestIoU = 0.25f; // Lowered threshold

            foreach (var person in _currentTrackedPersons)
            {
                var iou = CalculateIoU(newBox, person.BoundingBox);
                if (iou > bestIoU)
                {
                    bestIoU = iou;
                    best = person;
                }
            }

            return best;
        }

        private float CalculateIoU(BoundingBox a, BoundingBox b)
        {
            int x1 = Math.Max(a.X, b.X);
            int y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            int intersect = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            if (intersect == 0) return 0;

            int union = (a.Width * a.Height) + (b.Width * b.Height) - intersect;
            return (float)intersect / union;
        }

        private async Task DatabaseSaveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isConnected && !_disposed)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_persistenceSettings.SaveIntervalSeconds), ct);
                    if (!_persistenceSettings.SaveToDatabase) continue;

                    List<TrackedPerson> persons;
                    lock (_detectionLock) { persons = _currentTrackedPersons.ToList(); }

                    if (persons.Count > 0 || _lastSavedCount != 0)
                    {
                        await SaveToDatabaseAsync(persons, ct);
                        _lastSavedCount = persons.Count;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DB save error");
                }
            }
        }

        private async Task SaveToDatabaseAsync(List<TrackedPerson> persons, CancellationToken ct)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                // Use a short timeout
                context.Database.SetCommandTimeout(10);

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
                _logger.LogWarning(ex, "DB save failed - will retry");
            }
        }

        private void DrawAnnotations(Mat frame, List<TrackedPerson> persons)
        {
            var yellow = new Scalar(0, 255, 255);
            var white = new Scalar(255, 255, 255);
            var black = new Scalar(0, 0, 0);
            var font = HersheyFonts.HersheySimplex;
            var rand = new Random(42);

            foreach (var person in persons)
            {
                var color = new Scalar(rand.Next(100, 255), rand.Next(100, 255), rand.Next(100, 255));
                var box = person.BoundingBox;

                DrawCornerBox(frame, box.X, box.Y, box.Width, box.Height, color, 2, 20);

                var label = $"P-{person.GlobalPersonId.ToString()[..6]} {person.Confidence:P0}";
                var labelPos = new CvPoint(box.X, Math.Max(25, box.Y - 8));
                var textSize = Cv2.GetTextSize(label, font, 0.4, 1, out _);

                Cv2.Rectangle(frame, new Rect(labelPos.X - 2, labelPos.Y - textSize.Height - 4,
                    textSize.Width + 4, textSize.Height + 8), color, -1);
                Cv2.PutText(frame, label, labelPos, font, 0.4, black, 1, LineTypes.AntiAlias);
            }

            Cv2.Rectangle(frame, new Rect(5, 5, 180, 55), new Scalar(0, 0, 0, 200), -1);
            Cv2.PutText(frame, $"Current: {persons.Count} | Unique: {_uniquePersonCount}",
                new CvPoint(10, 25), font, 0.5, yellow, 1);
            Cv2.PutText(frame, $"{DateTime.Now:HH:mm:ss} | {_currentFps:F0} FPS",
                new CvPoint(10, 45), font, 0.4, white, 1);
        }

        private void DrawCornerBox(Mat frame, int x, int y, int w, int h, Scalar color, int t, int len)
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

        private async Task NotifyClientsAsync(List<TrackedPerson> persons)
        {
            try
            {
                var update = new
                {
                    cameraId = _cameraId,           // Use camelCase explicitly
                    count = persons.Count,
                    uniqueCount = _uniquePersonCount,
                    timestamp = DateTime.UtcNow,
                    fps = _currentFps,
                    persons = persons.Select(p => new
                    {
                        id = p.GlobalPersonId.ToString()[..8],
                        confidence = p.Confidence,
                        isNew = p.IsNew
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
            // Remove the _disposed check here - we want to disconnect even during disposal
            if (!_isConnected && _capture == null) return;

            _logger.LogInformation("⏹️ Disconnecting camera {Id}...", _cameraId);
            _isConnected = false;

            // Cancel all tasks
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException) { }

            // Wait for tasks to complete with timeout
            try
            {
                var allTasks = new List<Task>();
                if (_captureTask != null) allTasks.Add(_captureTask);
                if (_streamTask != null) allTasks.Add(_streamTask);
                if (_detectionTask != null) allTasks.Add(_detectionTask);
                if (_saveTask != null) allTasks.Add(_saveTask);

                if (allTasks.Count > 0)
                {
                    var completed = Task.WaitAll(allTasks.ToArray(), TimeSpan.FromSeconds(5));
                    if (!completed)
                    {
                        _logger.LogWarning("⚠️ Some tasks didn't complete in time for camera {Id}", _cameraId);
                    }
                }
            }
            catch (AggregateException ex)
            {
                // Tasks were cancelled - this is expected
                _logger.LogDebug("Tasks cancelled: {Message}", ex.InnerException?.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for tasks to complete");
            }

            // Dispose the capture
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
                        _logger.LogInformation("📷 Camera {Id} capture released", _cameraId);
                    }
                }
                finally
                {
                    _captureLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing capture for camera {Id}", _cameraId);
            }

            _logger.LogInformation("✅ Camera {Id} disconnected", _cameraId);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _logger.LogInformation("🗑️ Disposing camera {Id} processor...", _cameraId);

            // Disconnect first (stop capture and tasks)
            Disconnect();

            // Then dispose managed resources
            try
            {
                _cts?.Dispose();
            }
            catch { }

            try
            {
                _captureLock.Dispose();
            }
            catch { }

            try
            {
                _streamChannel.Writer.TryComplete();
            }
            catch { }

            // Clear state
            lock (_frameLock) { _currentJpeg = null; }
            lock (_detectionLock) { _currentTrackedPersons.Clear(); }

            _logger.LogInformation("✅ Camera {Id} processor disposed", _cameraId);
        }
    }
}