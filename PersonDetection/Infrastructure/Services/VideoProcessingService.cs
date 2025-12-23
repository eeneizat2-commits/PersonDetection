// PersonDetection.Infrastructure/Services/VideoProcessingService.cs
namespace PersonDetection.Infrastructure.Services
{
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Threading.Channels;
    using Microsoft.AspNetCore.SignalR;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using OpenCvSharp;
    using PersonDetection.API.Hubs;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.ValueObjects;
    using PersonDetection.Infrastructure.Context;
    using PersonDetection.Infrastructure.Detection;
    using PersonDetection.Infrastructure.ReId;

    public class VideoProcessingService : IVideoProcessingService, IDisposable
    {
        private readonly IDetectionEngine<YoloDetectionConfig> _detectionEngine;
        private readonly IReIdentificationEngine<OSNetConfig>? _reidEngine;
        private readonly IHubContext<DetectionHub> _hubContext;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<VideoProcessingService> _logger;

        private readonly ConcurrentDictionary<Guid, VideoIdentityMatcher> _videoMatchers = new();
        private readonly ConcurrentDictionary<Guid, VideoJobInternal> _jobs = new();
        private readonly Channel<VideoJobInternal> _processingQueue;
        private readonly CancellationTokenSource _serviceCts = new();
        private readonly Task _processingTask;

        private class VideoJobInternal
        {
            public Guid JobId { get; set; }
            public int DbId { get; set; }
            public string FilePath { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public int FrameSkip { get; set; } = 5;
            public bool ExtractFeatures { get; set; } = true;
            public bool SaveVideoToDb { get; set; } = false; // Default to false - don't save video data
            public VideoProcessingState State { get; set; } = VideoProcessingState.Queued;
            public int TotalFrames { get; set; }
            public int ProcessedFrames { get; set; }
            public int TotalPersonsDetected { get; set; }
            public HashSet<Guid> UniquePersons { get; set; } = new();
            public DateTime StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public string? ErrorMessage { get; set; }
            public List<VideoFrameDetectionDto> Detections { get; set; } = new();
            public CancellationTokenSource Cts { get; set; } = new();
            public double VideoDuration { get; set; }
            public double VideoFps { get; set; }
            public Stopwatch ProcessingStopwatch { get; set; } = new();
            public Dictionary<Guid, PersonTrackingInfo> PersonTracking { get; set; } = new();
            public Dictionary<Guid, byte[]> PersonThumbnails { get; set; } = new();
            public Dictionary<Guid, float[]> PersonFeatures { get; set; } = new();
        }

        private class PersonTrackingInfo
        {
            public Guid GlobalPersonId { get; set; }
            public double FirstAppearance { get; set; }
            public double LastAppearance { get; set; }
            public int TotalAppearances { get; set; }
            public float TotalConfidence { get; set; }
            public BoundingBox? BestBoundingBox { get; set; }
            public float BestConfidence { get; set; }
            public int BestFrameNumber { get; set; }
        }

        private class VideoIdentityMatcher
        {
            private readonly List<PersonIdentity> _identities = new();
            private readonly float _similarityThreshold;
            private readonly ILogger _logger;
            private readonly bool _debugMode;

            private readonly HashSet<Guid> _usedInCurrentFrame = new();
            private int _currentFrameNumber = -1;

            private class PersonIdentity
            {
                public Guid Id { get; set; }
                public float[] Features { get; set; } = null!;
                public int SightingCount { get; set; }
                public BoundingBox LastBoundingBox { get; set; } = null!;
                public int LastSeenFrame { get; set; }
            }

            public VideoIdentityMatcher(float similarityThreshold, ILogger logger, bool debugMode = true)
            {
                _similarityThreshold = similarityThreshold;
                _logger = logger;
                _debugMode = debugMode;
            }

            public void BeginFrame(int frameNumber)
            {
                if (frameNumber != _currentFrameNumber)
                {
                    _usedInCurrentFrame.Clear();
                    _currentFrameNumber = frameNumber;
                }
            }

            public (Guid Id, float[]? Features) GetOrCreateIdentity(float[]? features, BoundingBox boundingBox, int frameNumber)
            {
                BeginFrame(frameNumber);

                if (features == null || features.Length == 0 || !HasValidFeatures(features))
                {
                    _logger.LogWarning("Invalid or null features received, creating new identity");
                    var newId = CreateNewIdentity(null, boundingBox, frameNumber);
                    return (newId, null);
                }

                Guid bestMatch = Guid.Empty;
                float bestSimilarity = 0;
                int bestMatchIndex = -1;

                for (int i = 0; i < _identities.Count; i++)
                {
                    var identity = _identities[i];

                    if (_usedInCurrentFrame.Contains(identity.Id))
                    {
                        if (_debugMode)
                        {
                            _logger.LogDebug("Skipping identity {Id} - already used in frame {Frame}",
                                identity.Id.ToString()[..6], frameNumber);
                        }
                        continue;
                    }

                    var similarity = CosineSimilarity(features, identity.Features);

                    if (_debugMode)
                    {
                        _logger.LogDebug("Comparing with identity {Id}: similarity = {Sim:F4}",
                            identity.Id.ToString()[..6], similarity);
                    }

                    if (similarity > bestSimilarity)
                    {
                        bestSimilarity = similarity;
                        bestMatch = identity.Id;
                        bestMatchIndex = i;
                    }
                }

                if (_debugMode)
                {
                    _logger.LogInformation(
                        "Best match: {Match} with similarity {Sim:F4} (threshold: {Threshold:F4})",
                        bestMatch != Guid.Empty ? bestMatch.ToString()[..6] : "none",
                        bestSimilarity,
                        _similarityThreshold);
                }

                if (bestSimilarity >= _similarityThreshold && bestMatchIndex >= 0)
                {
                    var identity = _identities[bestMatchIndex];

                    var spatialDistance = CalculateBoundingBoxDistance(identity.LastBoundingBox, boundingBox);
                    var framesElapsed = frameNumber - identity.LastSeenFrame;
                    var maxAllowedDistance = Math.Max(250f, framesElapsed * 60f);

                    if (framesElapsed <= 3 && spatialDistance > maxAllowedDistance)
                    {
                        if (_debugMode)
                        {
                            _logger.LogInformation(
                                "🚫 Rejected match: High similarity ({Sim:F4}) but spatial distance " +
                                "({Dist:F0}px) exceeds max ({Max:F0}px) for {Frames} frame gap",
                                bestSimilarity, spatialDistance, maxAllowedDistance, framesElapsed);
                        }
                        var newId = CreateNewIdentity(features, boundingBox, frameNumber);
                        return (newId, features);
                    }

                    identity.SightingCount++;
                    identity.LastBoundingBox = boundingBox;
                    identity.LastSeenFrame = frameNumber;
                    UpdateFeatures(identity, features);
                    _usedInCurrentFrame.Add(bestMatch);

                    return (bestMatch, identity.Features);
                }

                var createdId = CreateNewIdentity(features, boundingBox, frameNumber);
                return (createdId, features);
            }

            private Guid CreateNewIdentity(float[]? features, BoundingBox boundingBox, int frameNumber)
            {
                var newId = Guid.NewGuid();

                if (features != null && HasValidFeatures(features))
                {
                    _identities.Add(new PersonIdentity
                    {
                        Id = newId,
                        Features = (float[])features.Clone(),
                        SightingCount = 1,
                        LastBoundingBox = boundingBox,
                        LastSeenFrame = frameNumber
                    });

                    if (_debugMode)
                    {
                        LogFeatureStats($"New identity {newId.ToString()[..6]}", features);
                    }
                }

                _usedInCurrentFrame.Add(newId);

                _logger.LogInformation("🆕 Created new identity: {Id} (total: {Count})",
                    newId.ToString()[..6], _identities.Count);

                return newId;
            }

            private static float CalculateBoundingBoxDistance(BoundingBox a, BoundingBox b)
            {
                var centerAx = a.X + a.Width / 2f;
                var centerAy = a.Y + a.Height / 2f;
                var centerBx = b.X + b.Width / 2f;
                var centerBy = b.Y + b.Height / 2f;

                return (float)Math.Sqrt(
                    Math.Pow(centerAx - centerBx, 2) +
                    Math.Pow(centerAy - centerBy, 2));
            }

            private bool HasValidFeatures(float[] features)
            {
                if (features.Length == 0) return false;

                float sum = 0;
                float min = float.MaxValue;
                float max = float.MinValue;

                foreach (var f in features)
                {
                    sum += Math.Abs(f);
                    if (f < min) min = f;
                    if (f > max) max = f;
                }

                bool hasVariance = (max - min) > 0.01f;
                bool hasValues = sum > 0.1f;

                return hasVariance && hasValues;
            }

            private void UpdateFeatures(PersonIdentity identity, float[] newFeatures)
            {
                const float alpha = 0.3f;
                for (int i = 0; i < identity.Features.Length && i < newFeatures.Length; i++)
                {
                    identity.Features[i] = identity.Features[i] * (1 - alpha) + newFeatures[i] * alpha;
                }
            }

            private void LogFeatureStats(string context, float[] features)
            {
                var min = features.Min();
                var max = features.Max();
                var mean = features.Average();
                var variance = features.Select(f => (f - mean) * (f - mean)).Average();
                var nonZeroCount = features.Count(f => Math.Abs(f) > 0.001f);

                _logger.LogInformation(
                    "[{Context}] Features: dim={Dim}, min={Min:F4}, max={Max:F4}, mean={Mean:F4}, var={Var:F6}, nonZero={NonZero}",
                    context, features.Length, min, max, mean, variance, nonZeroCount);
            }

            private static float CosineSimilarity(float[] a, float[] b)
            {
                if (a.Length != b.Length) return 0;

                float dot = 0, normA = 0, normB = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    dot += a[i] * b[i];
                    normA += a[i] * a[i];
                    normB += b[i] * b[i];
                }

                if (normA < 1e-10 || normB < 1e-10) return 0;

                var magnitude = (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
                return magnitude == 0 ? 0 : dot / magnitude;
            }

            public int UniqueCount => _identities.Count;

            public float[]? GetFeatures(Guid personId)
            {
                return _identities.FirstOrDefault(i => i.Id == personId)?.Features;
            }
        }

        public VideoProcessingService(
            IDetectionEngine<YoloDetectionConfig> detectionEngine,
            IReIdentificationEngine<OSNetConfig>? reidEngine,
            IHubContext<DetectionHub> hubContext,
            IServiceScopeFactory scopeFactory,
            ILogger<VideoProcessingService> logger)
        {
            _detectionEngine = detectionEngine;
            _reidEngine = reidEngine;
            _hubContext = hubContext;
            _scopeFactory = scopeFactory;
            _logger = logger;

            _processingQueue = Channel.CreateBounded<VideoJobInternal>(new BoundedChannelOptions(10)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

            _processingTask = Task.Run(ProcessQueueAsync);

            _logger.LogInformation("VideoProcessingService initialized. ReID engine: {ReId}",
                _reidEngine != null ? "Available" : "NOT AVAILABLE");
        }

        public async Task QueueVideoAsync(Guid jobId, string filePath, string fileName, int frameSkip, bool extractFeatures, CancellationToken ct)
        {
            if (extractFeatures && _reidEngine == null)
            {
                _logger.LogWarning("ReID requested but engine not available. Disabling feature extraction.");
                extractFeatures = false;
            }

            _logger.LogInformation("🎬 Queueing video job {JobId}: {FileName}", jobId.ToString()[..8], fileName);

            var job = new VideoJobInternal
            {
                JobId = jobId,
                FilePath = filePath,
                FileName = fileName,
                FrameSkip = Math.Max(1, frameSkip),
                ExtractFeatures = extractFeatures,
                SaveVideoToDb = false,
                State = VideoProcessingState.Queued,
                StartedAt = DateTime.UtcNow
            };

            _jobs[jobId] = job;
            _videoMatchers[jobId] = new VideoIdentityMatcher(0.70f, _logger, debugMode: true);

            // Create initial database record - MUST succeed before queueing
            await CreateVideoJobInDatabaseAsync(job, ct);

            if (job.DbId <= 0)
            {
                _logger.LogWarning("⚠️ VideoJob not saved to DB, but continuing with in-memory processing");
            }

            await _processingQueue.Writer.WriteAsync(job, ct);

            _logger.LogInformation("✅ Video job {JobId} queued successfully. DbId: {DbId}",
                jobId.ToString()[..8], job.DbId);
        }

        /// <summary>
        /// Create initial DB record - FAST (no video data)
        /// </summary>
        private async Task CreateVideoJobInDatabaseAsync(VideoJobInternal job, CancellationToken ct)
        {
            try
            {
                _logger.LogInformation("📝 Creating VideoJob in database for job {JobId}...", job.JobId.ToString()[..8]);

                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                // Check if already exists
                var existing = await context.VideoJobs.FirstOrDefaultAsync(v => v.JobId == job.JobId, ct);
                if (existing != null)
                {
                    _logger.LogWarning("VideoJob {JobId} already exists with DbId {DbId}", job.JobId, existing.Id);
                    job.DbId = existing.Id;
                    return;
                }

                var videoJob = new VideoJob
                {
                    JobId = job.JobId,
                    FileName = job.FileName,
                    OriginalFilePath = job.FilePath,
                    StoredFilePath = job.FilePath,
                    FrameSkip = job.FrameSkip,
                    State = VideoJobState.Queued,
                    CreatedAt = DateTime.UtcNow,
                    StartedAt = DateTime.UtcNow,
                    VideoDataBase64 = null,
                    TotalFrames = 0,
                    ProcessedFrames = 0,
                    TotalDetections = 0,
                    UniquePersonCount = 0,
                    VideoDurationSeconds = 0,
                    VideoFps = 0,
                    ProcessingTimeSeconds = 0
                };

                context.VideoJobs.Add(videoJob);
                await context.SaveChangesAsync(ct);

                job.DbId = videoJob.Id;
                _logger.LogInformation("✅ VideoJob created in database with Id: {Id}, JobId: {JobId}",
                    videoJob.Id, job.JobId.ToString()[..8]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to create VideoJob in database for job {JobId}. Error: {Error}",
                    job.JobId.ToString()[..8], ex.Message);

                // Log inner exception if exists
                if (ex.InnerException != null)
                {
                    _logger.LogError("Inner exception: {InnerError}", ex.InnerException.Message);
                }

                // Set DbId to -1 to indicate failure
                job.DbId = -1;
            }
        }

        private async Task ProcessQueueAsync()
        {
            _logger.LogInformation("🎬 Video processing queue started");

            await foreach (var job in _processingQueue.Reader.ReadAllAsync(_serviceCts.Token))
            {
                _logger.LogInformation("📥 Dequeued job {JobId} for processing", job.JobId.ToString()[..8]);

                try
                {
                    await ProcessVideoJobAsync(job);
                }
                catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
                {
                    job.State = VideoProcessingState.Cancelled;
                    await UpdateVideoJobStatusAsync(job);
                    _logger.LogInformation("Video job {JobId} cancelled", job.JobId);
                }
                catch (Exception ex)
                {
                    job.State = VideoProcessingState.Failed;
                    job.ErrorMessage = ex.Message;
                    job.CompletedAt = DateTime.UtcNow;
                    await UpdateVideoJobStatusAsync(job);
                    _logger.LogError(ex, "Video job {JobId} failed", job.JobId);
                }
                finally
                {
                    _videoMatchers.TryRemove(job.JobId, out _);
                }
            }

            _logger.LogInformation("🛑 Video processing queue stopped");
        }

        private async Task ProcessVideoJobAsync(VideoJobInternal job)
        {
            job.State = VideoProcessingState.Processing;
            job.ProcessingStopwatch.Start();

            await UpdateVideoJobStatusAsync(job);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts.Token, job.Cts.Token);
            var ct = linkedCts.Token;

            _logger.LogInformation("▶️ Starting video processing: {JobId} - {FileName}",
                job.JobId.ToString()[..8], job.FileName);

            using var capture = new VideoCapture(job.FilePath);

            if (!capture.IsOpened())
            {
                throw new InvalidOperationException($"Cannot open video file: {job.FilePath}");
            }

            job.TotalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
            job.VideoFps = capture.Get(VideoCaptureProperties.Fps);
            if (job.VideoFps <= 0) job.VideoFps = 30;
            job.VideoDuration = job.TotalFrames / job.VideoFps;

            _logger.LogInformation("📹 Video: {Frames} frames, {Fps:F1} FPS, {Duration:F1}s",
                job.TotalFrames, job.VideoFps, job.VideoDuration);

            var detectionConfig = new YoloDetectionConfig
            {
                ConfidenceThreshold = 0.4f,
                NmsThreshold = 0.45f,
                ModelInputSize = 640
            };

            var reidConfig = new OSNetConfig();
            var matcher = _videoMatchers[job.JobId];

            int frameNumber = 0;
            int processedCount = 0;

            using var frame = new Mat();

            while (capture.Read(frame) && !ct.IsCancellationRequested)
            {
                frameNumber++;

                if (frameNumber % job.FrameSkip != 0)
                    continue;

                if (frame.Empty())
                    continue;

                try
                {
                    Cv2.ImEncode(".jpg", frame, out var jpegData, new[] { (int)ImwriteFlags.JpegQuality, 90 });

                    if (jpegData == null || jpegData.Length < 1000)
                        continue;

                    var detections = await _detectionEngine.DetectAsync(jpegData, detectionConfig, ct);

                    _logger.LogDebug("Frame {Frame}: Detected {Count} persons", frameNumber, detections.Count);

                    var persons = new List<PersonDetectionDto>();

                    for (int i = 0; i < detections.Count; i++)
                    {
                        var detection = detections[i];
                        Guid globalPersonId;
                        float[]? features = null;

                        if (job.ExtractFeatures && _reidEngine != null)
                        {
                            try
                            {
                                var featureVector = await _reidEngine.ExtractFeaturesAsync(
                                    jpegData, detection.BoundingBox, reidConfig, ct);

                                features = featureVector.Values;

                                var (personId, returnedFeatures) = matcher.GetOrCreateIdentity(features, detection.BoundingBox, frameNumber);
                                globalPersonId = personId;

                                if (returnedFeatures != null && !job.PersonFeatures.ContainsKey(globalPersonId))
                                {
                                    job.PersonFeatures[globalPersonId] = returnedFeatures;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "ReID failed for frame {Frame}, person {Index}", frameNumber, i);
                                globalPersonId = Guid.NewGuid();
                            }
                        }
                        else
                        {
                            globalPersonId = Guid.NewGuid();
                        }

                        job.UniquePersons.Add(globalPersonId);
                        job.TotalPersonsDetected++;

                        var timestamp = frameNumber / job.VideoFps;
                        if (!job.PersonTracking.TryGetValue(globalPersonId, out var tracking))
                        {
                            tracking = new PersonTrackingInfo
                            {
                                GlobalPersonId = globalPersonId,
                                FirstAppearance = timestamp,
                                LastAppearance = timestamp,
                                TotalAppearances = 0,
                                TotalConfidence = 0,
                                BestConfidence = 0
                            };
                            job.PersonTracking[globalPersonId] = tracking;
                        }

                        tracking.LastAppearance = timestamp;
                        tracking.TotalAppearances++;
                        tracking.TotalConfidence += detection.Confidence;

                        if (detection.Confidence > tracking.BestConfidence)
                        {
                            tracking.BestConfidence = detection.Confidence;
                            tracking.BestBoundingBox = detection.BoundingBox;
                            tracking.BestFrameNumber = frameNumber;

                            try
                            {
                                var thumbnail = ExtractThumbnail(frame, detection.BoundingBox);
                                if (thumbnail != null)
                                {
                                    job.PersonThumbnails[globalPersonId] = thumbnail;
                                }
                            }
                            catch { }
                        }

                        persons.Add(new PersonDetectionDto(
                            0,
                            new BoundingBoxDto(
                                detection.BoundingBox.X,
                                detection.BoundingBox.Y,
                                detection.BoundingBox.Width,
                                detection.BoundingBox.Height,
                                detection.BoundingBox.AspectRatio),
                            detection.Confidence,
                            globalPersonId,
                            null,
                            DateTime.UtcNow
                        ));
                    }

                    var frameDetection = new VideoFrameDetectionDto(
                        frameNumber,
                        frameNumber / job.VideoFps,
                        detections.Count,
                        persons
                    );

                    job.Detections.Add(frameDetection);
                    processedCount++;
                    job.ProcessedFrames = processedCount;

                    if (processedCount % 10 == 0)
                    {
                        _logger.LogInformation(
                            "📊 Progress: Frame {Frame}/{Total}, {Unique} unique persons so far",
                            frameNumber, job.TotalFrames, matcher.UniqueCount);
                    }

                    if (processedCount % 5 == 0)
                    {
                        await NotifyProgressAsync(job, matcher.UniqueCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing frame {Frame}", frameNumber);
                }
            }

            job.ProcessingStopwatch.Stop();
            job.State = ct.IsCancellationRequested ? VideoProcessingState.Cancelled : VideoProcessingState.Completed;
            job.CompletedAt = DateTime.UtcNow;

            // Save all results to database
            await SaveVideoResultsToDatabaseAsync(job, matcher);

            await NotifyCompletionAsync(job);

            _logger.LogInformation(
                "✅ Video completed: {JobId} - {Frames} frames, {Detections} detections, {Unique} unique persons in {Time:F1}s",
                job.JobId.ToString()[..8], processedCount, job.TotalPersonsDetected,
                matcher.UniqueCount, job.ProcessingStopwatch.Elapsed.TotalSeconds);
        }

        private byte[]? ExtractThumbnail(Mat frame, BoundingBox bbox)
        {
            try
            {
                int padding = 10;
                int x = Math.Max(0, bbox.X - padding);
                int y = Math.Max(0, bbox.Y - padding);
                int width = Math.Min(frame.Width - x, bbox.Width + 2 * padding);
                int height = Math.Min(frame.Height - y, bbox.Height + 2 * padding);

                if (width <= 0 || height <= 0) return null;

                using var cropped = new Mat(frame, new Rect(x, y, width, height));
                using var resized = new Mat();
                Cv2.Resize(cropped, resized, new Size(128, 256));

                Cv2.ImEncode(".jpg", resized, out var jpegData, new[] { (int)ImwriteFlags.JpegQuality, 85 });
                return jpegData;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Save all results after processing completes
        /// </summary>
        private async Task SaveVideoResultsToDatabaseAsync(VideoJobInternal job, VideoIdentityMatcher matcher)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                _logger.LogInformation("💾 Saving video results to database...");

                // 1. Update VideoJob record
                var videoJob = await context.VideoJobs.FirstOrDefaultAsync(v => v.JobId == job.JobId);
                if (videoJob == null)
                {
                    _logger.LogWarning("VideoJob not found in database: {JobId}", job.JobId);
                    return;
                }

                // Calculate statistics
                var peakCount = job.Detections.Any() ? job.Detections.Max(d => d.PersonCount) : 0;
                var avgCount = job.Detections.Any() ? job.Detections.Average(d => d.PersonCount) : 0;


                videoJob.State = (VideoJobState)(int)job.State;
                videoJob.TotalFrames = job.TotalFrames;
                videoJob.ProcessedFrames = job.ProcessedFrames;
                videoJob.TotalDetections = job.TotalPersonsDetected;
                videoJob.UniquePersonCount = matcher.UniqueCount;
                videoJob.VideoDurationSeconds = job.VideoDuration;
                videoJob.VideoFps = job.VideoFps;
                videoJob.ProcessingTimeSeconds = job.ProcessingStopwatch.Elapsed.TotalSeconds;
                videoJob.CompletedAt = DateTime.UtcNow;
                videoJob.AveragePersonsPerFrame = avgCount;  // 👈 ADD THIS
                videoJob.PeakPersonCount = peakCount;

                if (job.State == VideoProcessingState.Failed)
                    videoJob.ErrorMessage = job.ErrorMessage;

                await context.SaveChangesAsync();
                _logger.LogInformation("✅ Updated VideoJob record");

                // 2. Save VideoPersonTimelines
                foreach (var tracking in job.PersonTracking.Values)
                {
                    var uniquePerson = await context.UniquePersons
                        .FirstOrDefaultAsync(u => u.GlobalPersonId == tracking.GlobalPersonId);

                    int? uniquePersonId = null;

                    if (uniquePerson == null)
                    {
                        job.PersonFeatures.TryGetValue(tracking.GlobalPersonId, out var features);
                        job.PersonThumbnails.TryGetValue(tracking.GlobalPersonId, out var thumbnail);

                        uniquePerson = UniquePerson.Create(
                            tracking.GlobalPersonId,
                            0,
                            features,
                            thumbnail
                        );

                        context.UniquePersons.Add(uniquePerson);
                        await context.SaveChangesAsync();
                    }
                    else
                    {
                        uniquePerson.UpdateLastSeen(0, job.PersonFeatures.GetValueOrDefault(tracking.GlobalPersonId));
                    }

                    uniquePersonId = uniquePerson.Id;

                    job.PersonThumbnails.TryGetValue(tracking.GlobalPersonId, out var thumbData);
                    job.PersonFeatures.TryGetValue(tracking.GlobalPersonId, out var featData);

                    var timeline = VideoPersonTimeline.Create(
                        videoJob.Id,
                        tracking.GlobalPersonId,
                        tracking.FirstAppearance,
                        tracking.LastAppearance,
                        tracking.TotalAppearances,
                        tracking.TotalAppearances > 0 ? tracking.TotalConfidence / tracking.TotalAppearances : 0,
                        featData,
                        thumbData
                    );
                    timeline.UniquePersonId = uniquePersonId;

                    context.VideoPersonTimelines.Add(timeline);
                }

                await context.SaveChangesAsync();
                _logger.LogInformation("✅ Saved {Count} person timelines", job.PersonTracking.Count);

                // 3. Save sampled DetectionResults (max ~50 records)
                var sampleRate = Math.Max(1, job.Detections.Count / 50);
                var sampledDetections = job.Detections
                    .Where((d, idx) => idx % sampleRate == 0)
                    .ToList();

                foreach (var frameDetection in sampledDetections)
                {
                    var detectionResult = new DetectionResult
                    {
                        CameraId = 0,
                        VideoJobId = videoJob.Id,
                        Timestamp = job.StartedAt.AddSeconds(frameDetection.TimestampSeconds),
                        TotalDetections = frameDetection.PersonCount,
                        ValidDetections = frameDetection.PersonCount,
                        UniquePersonCount = frameDetection.Persons.Select(p => p.GlobalPersonId).Distinct().Count()
                    };

                    context.DetectionResults.Add(detectionResult);
                    await context.SaveChangesAsync();

                    foreach (var person in frameDetection.Persons)
                    {
                        job.PersonFeatures.TryGetValue(person.GlobalPersonId, out var features);

                        var detectedPerson = new DetectedPerson
                        {
                            GlobalPersonId = person.GlobalPersonId,
                            Confidence = person.Confidence,
                            BoundingBox_X = person.BoundingBox.X,
                            BoundingBox_Y = person.BoundingBox.Y,
                            BoundingBox_Width = person.BoundingBox.Width,
                            BoundingBox_Height = person.BoundingBox.Height,
                            FeatureVector = features != null ? string.Join(",", features) : null,
                            DetectedAt = job.StartedAt.AddSeconds(frameDetection.TimestampSeconds),
                            DetectionResultId = detectionResult.Id,
                            VideoJobId = videoJob.Id,
                            FrameNumber = frameDetection.FrameNumber,
                            TimestampSeconds = frameDetection.TimestampSeconds
                        };

                        context.DetectedPersons.Add(detectedPerson);
                    }
                }

                await context.SaveChangesAsync();
                _logger.LogInformation("✅ Saved {Count} sampled detection results", sampledDetections.Count);

                _logger.LogInformation("💾 Video results saved successfully!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save video results to database");
            }
        }

        private async Task UpdateVideoJobStatusAsync(VideoJobInternal job)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                var videoJob = await context.VideoJobs.FirstOrDefaultAsync(v => v.JobId == job.JobId);
                if (videoJob != null)
                {
                    videoJob.State = (VideoJobState)(int)job.State;
                    videoJob.ProcessedFrames = job.ProcessedFrames;
                    videoJob.TotalFrames = job.TotalFrames;
                    videoJob.TotalDetections = job.TotalPersonsDetected;
                    videoJob.UniquePersonCount = job.UniquePersons.Count;

                    if (job.State == VideoProcessingState.Failed)
                    {
                        videoJob.ErrorMessage = job.ErrorMessage;
                        videoJob.CompletedAt = DateTime.UtcNow;
                    }
                    else if (job.State == VideoProcessingState.Cancelled)
                    {
                        videoJob.CompletedAt = DateTime.UtcNow;
                    }

                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update video job status in database");
            }
        }

        private async Task NotifyProgressAsync(VideoJobInternal job, int uniqueCount)
        {
            try
            {
                var progress = job.TotalFrames > 0
                    ? (int)((double)job.ProcessedFrames / (job.TotalFrames / job.FrameSkip) * 100)
                    : 0;

                await _hubContext.Clients.All.SendAsync("VideoProcessingProgress", new
                {
                    JobId = job.JobId,
                    FileName = job.FileName,
                    Progress = Math.Min(progress, 100),
                    ProcessedFrames = job.ProcessedFrames,
                    TotalPersonsDetected = job.TotalPersonsDetected,
                    UniquePersons = uniqueCount
                });
            }
            catch { }
        }

        private async Task NotifyCompletionAsync(VideoJobInternal job)
        {
            try
            {
                var uniqueCount = _videoMatchers.TryGetValue(job.JobId, out var m) ? m.UniqueCount : job.UniquePersons.Count;

                await _hubContext.Clients.All.SendAsync("VideoProcessingComplete", new
                {
                    JobId = job.JobId,
                    FileName = job.FileName,
                    State = job.State.ToString(),
                    TotalPersonsDetected = job.TotalPersonsDetected,
                    UniquePersons = uniqueCount,
                    ProcessingTimeSeconds = job.ProcessingStopwatch.Elapsed.TotalSeconds
                });
            }
            catch { }
        }

        public VideoProcessingStatusDto? GetStatus(Guid jobId)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                return null;

            var progress = job.TotalFrames > 0
                ? (int)((double)job.ProcessedFrames / (job.TotalFrames / job.FrameSkip) * 100)
                : 0;

            var uniqueCount = _videoMatchers.TryGetValue(jobId, out var matcher)
                ? matcher.UniqueCount
                : job.UniquePersons.Count;

            return new VideoProcessingStatusDto(
                job.JobId,
                job.FileName,
                job.State,
                job.TotalFrames,
                job.ProcessedFrames,
                Math.Min(progress, 100),
                job.TotalPersonsDetected,
                uniqueCount,
                job.StartedAt,
                job.CompletedAt,
                job.ErrorMessage,
                job.Detections.TakeLast(100).ToList()
            );
        }

        public VideoProcessingSummaryDto? GetSummary(Guid jobId)
        {
            if (!_jobs.TryGetValue(jobId, out var job))
                return null;

            if (job.State != VideoProcessingState.Completed)
                return null;

            var peakCount = job.Detections.Any() ? job.Detections.Max(d => d.PersonCount) : 0;
            var avgCount = job.Detections.Any() ? job.Detections.Average(d => d.PersonCount) : 0;

            var timelines = job.PersonTracking.Values
                .OrderBy(t => t.FirstAppearance)
                .Select(t => new PersonTimelineDto(
                    t.GlobalPersonId,
                    t.GlobalPersonId.ToString()[..6],
                    t.FirstAppearance,
                    t.LastAppearance,
                    t.TotalAppearances,
                    t.TotalAppearances > 0 ? t.TotalConfidence / t.TotalAppearances : 0
                ))
                .ToList();

            return new VideoProcessingSummaryDto(
                job.JobId,
                job.FileName,
                TimeSpan.FromSeconds(job.VideoDuration),
                job.ProcessedFrames,
                job.TotalPersonsDetected,
                job.PersonTracking.Count,
                avgCount,
                peakCount,
                job.ProcessingStopwatch.Elapsed.TotalSeconds,
                timelines
            );
        }

        public IEnumerable<VideoProcessingStatusDto> GetAllJobs()
        {
            return _jobs.Values.Select(job =>
            {
                var progress = job.TotalFrames > 0
                    ? (int)((double)job.ProcessedFrames / (job.TotalFrames / job.FrameSkip) * 100)
                    : 0;

                var uniqueCount = _videoMatchers.TryGetValue(job.JobId, out var matcher)
                    ? matcher.UniqueCount
                    : job.PersonTracking.Count;

                return new VideoProcessingStatusDto(
                    job.JobId,
                    job.FileName,
                    job.State,
                    job.TotalFrames,
                    job.ProcessedFrames,
                    Math.Min(progress, 100),
                    job.TotalPersonsDetected,
                    uniqueCount,
                    job.StartedAt,
                    job.CompletedAt,
                    job.ErrorMessage,
                    new List<VideoFrameDetectionDto>()
                );
            });
        }

        public bool CancelJob(Guid jobId)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Cts.Cancel();
                return true;
            }
            return false;
        }

        public void CleanupJob(Guid jobId)
        {
            if (_jobs.TryRemove(jobId, out var job))
            {
                job.Cts.Dispose();
                _videoMatchers.TryRemove(jobId, out _);
                //try { if (File.Exists(job.FilePath)) File.Delete(job.FilePath); } catch { }
            }
        }

        public void Dispose()
        {
            _serviceCts.Cancel();
            _processingQueue.Writer.Complete();
            try { _processingTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
            foreach (var job in _jobs.Values) job.Cts.Dispose();
            _jobs.Clear();
            _videoMatchers.Clear();
            _serviceCts.Dispose();
        }
    }
}