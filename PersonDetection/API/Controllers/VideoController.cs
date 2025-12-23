namespace PersonDetection.API.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;
    using PersonDetection.Application.Commands;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Application.Queries;
    using PersonDetection.Application.Services;
    using PersonDetection.Infrastructure.Context;
    using PersonDetection.Infrastructure.Detection;
    using PersonDetection.Infrastructure.ReId;

    [ApiController]
    [Route("api/[controller]")]
    public class VideoController : ControllerBase
    {
        private readonly ICommandDispatcher _commandDispatcher;
        private readonly IQueryDispatcher _queryDispatcher;
        private readonly IVideoProcessingService _videoService;
        private readonly ILogger<VideoController> _logger;
        private readonly IWebHostEnvironment _env;

        private static readonly string[] AllowedExtensions = { ".mp4", ".avi", ".mov", ".mkv", ".webm", ".wmv" };
        private const long MaxFileSize = 500 * 1024 * 1024; // 500 MB

        public VideoController(
            ICommandDispatcher commandDispatcher,
            IQueryDispatcher queryDispatcher,
            IVideoProcessingService videoService,
            IWebHostEnvironment env,
            ILogger<VideoController> logger)
        {
            _commandDispatcher = commandDispatcher;
            _queryDispatcher = queryDispatcher;
            _videoService = videoService;
            _env = env;
            _logger = logger;
        }

        /// <summary>
        /// Upload a video file for person detection
        /// </summary>
        /// <param name="file">Video file (MP4, AVI, MOV, MKV, WebM, WMV)</param>
        /// <param name="frameSkip">Process every Nth frame (default: 5)</param>
        /// <param name="extractFeatures">Enable ReID feature extraction (default: true)</param>
        [HttpPost("upload")]
        [RequestSizeLimit(500 * 1024 * 1024)] // 500 MB
        [RequestFormLimits(MultipartBodyLengthLimit = 500 * 1024 * 1024)]
        public async Task<ActionResult<VideoUploadResultDto>> UploadVideo(
            IFormFile file,
            [FromQuery] int frameSkip = 5,
            [FromQuery] bool extractFeatures = true,
            CancellationToken ct = default)
        {
            try
            {
                // Validate file
                if (file == null || file.Length == 0)
                    return BadRequest(new { error = "No file uploaded" });

                if (file.Length > MaxFileSize)
                    return BadRequest(new { error = $"File too large. Maximum size is {MaxFileSize / 1024 / 1024} MB" });

                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(extension))
                    return BadRequest(new { error = $"Invalid file type. Allowed: {string.Join(", ", AllowedExtensions)}" });

                // Create upload directory
                var uploadsDir = Path.Combine(_env.ContentRootPath, "uploads", "videos");
                Directory.CreateDirectory(uploadsDir);

                // Generate unique filename
                var jobId = Guid.NewGuid();
                var fileName = $"{jobId}{extension}";
                var filePath = Path.Combine(uploadsDir, fileName);

                // Save file
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream, ct);
                }

                _logger.LogInformation("Video uploaded: {FileName}, Size: {Size} bytes", file.FileName, file.Length);

                // Queue for processing
                var command = new ProcessVideoCommand(jobId, filePath, file.FileName, frameSkip, extractFeatures);
                var result = await _commandDispatcher.Dispatch(command, ct);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video upload failed");
                return StatusCode(500, new { error = "Failed to upload video", details = ex.Message });
            }
        }

        /// <summary>
        /// Get processing status for a video job
        /// </summary>
        [HttpGet("{jobId}/status")]
        public async Task<ActionResult<VideoProcessingStatusDto>> GetStatus(Guid jobId, CancellationToken ct)
        {
            var query = new GetVideoStatusQuery(jobId);
            var status = await _queryDispatcher.Dispatch(query, ct);

            if (status == null)
                return NotFound(new { error = "Job not found" });

            return Ok(status);
        }

        /// <summary>
        /// Get detailed summary after processing completes
        /// </summary>
        [HttpGet("{jobId}/summary")]
        public async Task<ActionResult<VideoProcessingSummaryDto>> GetSummary(Guid jobId, CancellationToken ct)
        {
            var query = new GetVideoSummaryQuery(jobId);
            var summary = await _queryDispatcher.Dispatch(query, ct);

            if (summary == null)
                return NotFound(new { error = "Summary not available. Job may not be completed yet." });

            return Ok(summary);
        }

        /// <summary>
        /// Get all video processing jobs
        /// </summary>
        [HttpGet("jobs")]
        public async Task<ActionResult<List<VideoProcessingStatusDto>>> GetAllJobs(CancellationToken ct)
        {
            var query = new GetAllVideoJobsQuery();
            var jobs = await _queryDispatcher.Dispatch(query, ct);
            return Ok(jobs);
        }

        /// <summary>
        /// Cancel a processing job
        /// </summary>
        [HttpPost("{jobId}/cancel")]
        public ActionResult CancelJob(Guid jobId)
        {
            var cancelled = _videoService.CancelJob(jobId);

            if (!cancelled)
                return NotFound(new { error = "Job not found" });

            return Ok(new { message = "Job cancellation requested", jobId });
        }

        /// <summary>
        /// Delete a completed job and its data
        /// </summary>
        [HttpDelete("{jobId}")]
        public ActionResult DeleteJob(Guid jobId)
        {
            var status = _videoService.GetStatus(jobId);

            if (status == null)
                return NotFound(new { error = "Job not found" });

            if (status.State == VideoProcessingState.Processing)
                return BadRequest(new { error = "Cannot delete a job that is still processing. Cancel it first." });

            _videoService.CleanupJob(jobId);

            return Ok(new { message = "Job deleted", jobId });
        }

        /// <summary>
        /// Get detections for a specific frame range
        /// </summary>
        [HttpGet("{jobId}/detections")]
        public ActionResult GetDetections(
            Guid jobId,
            [FromQuery] int startFrame = 0,
            [FromQuery] int endFrame = int.MaxValue,
            [FromQuery] int limit = 100)
        {
            var status = _videoService.GetStatus(jobId);

            if (status == null)
                return NotFound(new { error = "Job not found" });

            var detections = status.Detections
                .Where(d => d.FrameNumber >= startFrame && d.FrameNumber <= endFrame)
                .Take(limit)
                .ToList();

            return Ok(new
            {
                JobId = jobId,
                TotalDetections = status.Detections.Count,
                ReturnedCount = detections.Count,
                Detections = detections
            });
        }
        // Add to VideoController.cs

        [HttpGet("test-reid")]
        public async Task<IActionResult> TestReId()
        {
            var reidEngine = HttpContext.RequestServices.GetService<IReIdentificationEngine<OSNetConfig>>();

            if (reidEngine == null)
                return Ok(new { status = "ReID engine NOT available" });

            return Ok(new
            {
                status = "ReID engine available",
                dimension = reidEngine.VectorDimension
            });
        }
        // Add to VideoController.cs

        [HttpPost("test-reid-crop")]
        public async Task<IActionResult> TestReIdCrop(IFormFile file, CancellationToken ct)
        {
            var reidEngine = HttpContext.RequestServices.GetService<IReIdentificationEngine<OSNetConfig>>();
            var detectionEngine = HttpContext.RequestServices.GetService<IDetectionEngine<YoloDetectionConfig>>();

            if (reidEngine == null || detectionEngine == null)
                return BadRequest(new { error = "Engines not available" });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var imageData = ms.ToArray();

            // Detect persons
            var detections = await detectionEngine.DetectAsync(imageData, new YoloDetectionConfig(), ct);

            var results = new List<object>();
            var config = new OSNetConfig();

            foreach (var detection in detections)
            {
                var features = await reidEngine.ExtractFeaturesAsync(imageData, detection.BoundingBox, config, ct);

                results.Add(new
                {
                    BoundingBox = new
                    {
                        detection.BoundingBox.X,
                        detection.BoundingBox.Y,
                        detection.BoundingBox.Width,
                        detection.BoundingBox.Height
                    },
                    Confidence = detection.Confidence,
                    FeatureStats = new
                    {
                        Min = features.Values.Min(),
                        Max = features.Values.Max(),
                        Mean = features.Values.Average(),
                        First5 = features.Values.Take(5).ToArray()
                    }
                });
            }

            // Calculate similarities between all pairs
            var similarities = new List<object>();
            for (int i = 0; i < results.Count; i++)
            {
                for (int j = i + 1; j < results.Count; j++)
                {
                    var featI = ((dynamic)results[i]).FeatureStats.First5 as float[];
                    var featJ = ((dynamic)results[j]).FeatureStats.First5 as float[];
                    // For full comparison, extract from detection again
                }
            }

            return Ok(new
            {
                DetectionCount = detections.Count,
                Detections = results,
                Message = results.Count >= 2
                    ? "Check if First5 features are DIFFERENT for different people"
                    : "Need at least 2 people to compare"
            });
        }
        // Add to VideoController.cs

        /// <summary>
        /// Get video job from database (persisted data)
        /// </summary>
        [HttpGet("{jobId}/db")]
        public async Task<IActionResult> GetVideoJobFromDb(Guid jobId, CancellationToken ct)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var videoJob = await context.VideoJobs
                .Include(v => v.PersonTimelines)
                .FirstOrDefaultAsync(v => v.JobId == jobId, ct);

            if (videoJob == null)
                return NotFound(new { error = "Video job not found in database" });

            return Ok(new
            {
                videoJob.Id,
                videoJob.JobId,
                videoJob.FileName,
                State = videoJob.State.ToString(),
                videoJob.TotalFrames,
                videoJob.ProcessedFrames,
                videoJob.TotalDetections,
                videoJob.UniquePersonCount,
                videoJob.VideoDurationSeconds,
                videoJob.VideoFps,
                videoJob.ProcessingTimeSeconds,
                videoJob.StartedAt,
                videoJob.CompletedAt,
                videoJob.ErrorMessage,
                HasVideoData = !string.IsNullOrEmpty(videoJob.VideoDataBase64),
                PersonTimelines = videoJob.PersonTimelines.Select(t => new
                {
                    t.Id,
                    t.GlobalPersonId,
                    ShortId = t.GlobalPersonId.ToString()[..6],
                    t.FirstAppearanceSeconds,
                    t.LastAppearanceSeconds,
                    t.TotalAppearances,
                    t.AverageConfidence,
                    HasThumbnail = !string.IsNullOrEmpty(t.ThumbnailBase64)
                })
            });
        }

        /// <summary>
        /// Stream the original video file
        /// </summary>
        [HttpGet("{jobId}/video")]
        public async Task<IActionResult> GetVideo(Guid jobId, CancellationToken ct)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var videoJob = await context.VideoJobs
                .FirstOrDefaultAsync(v => v.JobId == jobId, ct);

            if (videoJob == null)
                return NotFound(new { error = "Video job not found" });

            var filePath = videoJob.StoredFilePath ?? videoJob.OriginalFilePath;

            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return NotFound(new { error = "Video file not found on disk", path = filePath });

            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var contentType = GetContentType(videoJob.FileName);

            return File(stream, contentType, videoJob.FileName, enableRangeProcessing: true);
        }

        /// <summary>
        /// Get video job details from database
        /// </summary>
        [HttpGet("{jobId}/details")]
        public async Task<IActionResult> GetVideoDetails(Guid jobId, CancellationToken ct)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var videoJob = await context.VideoJobs
                .Include(v => v.PersonTimelines)
                .FirstOrDefaultAsync(v => v.JobId == jobId, ct);

            if (videoJob == null)
                return NotFound(new { error = "Video job not found" });

            return Ok(new
            {
                videoJob.Id,
                videoJob.JobId,
                videoJob.FileName,
                State = videoJob.State.ToString(),
                videoJob.TotalFrames,
                videoJob.ProcessedFrames,
                videoJob.TotalDetections,
                videoJob.UniquePersonCount,
                videoJob.VideoDurationSeconds,
                videoJob.VideoFps,
                videoJob.ProcessingTimeSeconds,
                videoJob.FrameSkip,
                videoJob.AveragePersonsPerFrame,  // 👈 ADD THIS
                videoJob.PeakPersonCount,
                videoJob.StartedAt,
                videoJob.CompletedAt,
                videoJob.ErrorMessage,
                VideoFileExists = !string.IsNullOrEmpty(videoJob.StoredFilePath ?? videoJob.OriginalFilePath)
                                  && System.IO.File.Exists(videoJob.StoredFilePath ?? videoJob.OriginalFilePath),
                PersonTimelines = videoJob.PersonTimelines.Select(t => new
                {
                    t.Id,
                    t.GlobalPersonId,
                    ShortId = t.GlobalPersonId.ToString()[..6],
                    t.FirstAppearanceSeconds,
                    t.LastAppearanceSeconds,
                    t.TotalAppearances,
                    t.AverageConfidence,
                    HasThumbnail = !string.IsNullOrEmpty(t.ThumbnailBase64),
                    ThumbnailUrl = !string.IsNullOrEmpty(t.ThumbnailBase64)
                        ? $"/api/Video/{jobId}/person/{t.GlobalPersonId}/thumbnail"
                        : null
                }).OrderBy(t => t.FirstAppearanceSeconds)
            });
        }

        /// <summary>
        /// Get all video jobs from database with pagination
        /// </summary>
        [HttpGet("history")]
        public async Task<IActionResult> GetVideoHistory(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken ct = default)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var query = context.VideoJobs.OrderByDescending(v => v.CreatedAt);

            var total = await query.CountAsync(ct);
            var items = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(v => new
                {
                    v.Id,
                    v.JobId,
                    v.FileName,
                    State = v.State.ToString(),
                    v.TotalFrames,
                    v.ProcessedFrames,
                    v.TotalDetections,
                    v.UniquePersonCount,
                    v.VideoDurationSeconds,
                    v.ProcessingTimeSeconds,
                    v.StartedAt,
                    v.CompletedAt,
                    v.CreatedAt
                })
                .ToListAsync(ct);

            return Ok(new
            {
                Total = total,
                Page = page,
                PageSize = pageSize,
                TotalPages = (int)Math.Ceiling(total / (double)pageSize),
                Items = items
            });
        }

        private static string GetContentType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".mkv" => "video/x-matroska",
                ".wmv" => "video/x-ms-wmv",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Get all video jobs from database
        /// </summary>
        [HttpGet("db/all")]
        public async Task<IActionResult> GetAllVideoJobsFromDb(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken ct = default)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var query = context.VideoJobs
                .OrderByDescending(v => v.CreatedAt);

            var total = await query.CountAsync(ct);
            var items = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(v => new
                {
                    v.Id,
                    v.JobId,
                    v.FileName,
                    State = v.State.ToString(),
                    v.TotalFrames,
                    v.ProcessedFrames,
                    v.TotalDetections,
                    v.UniquePersonCount,
                    v.VideoDurationSeconds,
                    v.ProcessingTimeSeconds,
                    v.StartedAt,
                    v.CompletedAt,
                    v.CreatedAt
                })
                .ToListAsync(ct);

            return Ok(new
            {
                Total = total,
                Page = page,
                PageSize = pageSize,
                TotalPages = (int)Math.Ceiling(total / (double)pageSize),
                Items = items
            });
        }

        /// <summary>
        /// Get person thumbnail from video
        /// </summary>
        [HttpGet("{jobId}/person/{globalPersonId}/thumbnail")]
        public async Task<IActionResult> GetPersonThumbnail(Guid jobId, Guid globalPersonId, CancellationToken ct)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var timeline = await context.VideoPersonTimelines
                .FirstOrDefaultAsync(t => t.VideoJob!.JobId == jobId && t.GlobalPersonId == globalPersonId, ct);

            if (timeline == null || string.IsNullOrEmpty(timeline.ThumbnailBase64))
                return NotFound(new { error = "Thumbnail not found" });

            var bytes = Convert.FromBase64String(timeline.ThumbnailBase64);
            return File(bytes, "image/jpeg");
        }

        /// <summary>
        /// Download original video (if stored)
        /// </summary>
        [HttpGet("{jobId}/download")]
        public async Task<IActionResult> DownloadVideo(Guid jobId, CancellationToken ct)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var videoJob = await context.VideoJobs
                .FirstOrDefaultAsync(v => v.JobId == jobId, ct);

            if (videoJob == null)
                return NotFound(new { error = "Video job not found" });

            if (string.IsNullOrEmpty(videoJob.VideoDataBase64))
                return NotFound(new { error = "Video data not stored in database" });

            var bytes = Convert.FromBase64String(videoJob.VideoDataBase64);
            var extension = Path.GetExtension(videoJob.FileName);

            return File(bytes, "video/mp4", videoJob.FileName);
        }

        /// <summary>
        /// Get detections for a video from database
        /// </summary>
        [HttpGet("{jobId}/db/detections")]
        public async Task<IActionResult> GetVideoDetectionsFromDb(
            Guid jobId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken ct = default)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var videoJob = await context.VideoJobs.FirstOrDefaultAsync(v => v.JobId == jobId, ct);
            if (videoJob == null)
                return NotFound(new { error = "Video job not found" });

            var query = context.DetectedPersons
                .Where(d => d.VideoJobId == videoJob.Id)
                .OrderBy(d => d.FrameNumber);

            var total = await query.CountAsync(ct);
            var items = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(d => new
                {
                    d.Id,
                    d.GlobalPersonId,
                    ShortId = d.GlobalPersonId.ToString().Substring(0, 6),
                    d.FrameNumber,
                    d.TimestampSeconds,
                    d.Confidence,
                    BoundingBox = new
                    {
                        d.BoundingBox_X,
                        d.BoundingBox_Y,
                        d.BoundingBox_Width,
                        d.BoundingBox_Height
                    },
                    d.DetectedAt
                })
                .ToListAsync(ct);

            return Ok(new
            {
                VideoJobId = videoJob.Id,
                Total = total,
                Page = page,
                PageSize = pageSize,
                Items = items
            });
        }
        [HttpGet("debug/service-status")]
        public IActionResult DebugServiceStatus()
        {
            try
            {
                var jobs = _videoService.GetAllJobs().ToList();

                return Ok(new
                {
                    ServiceAvailable = true,
                    JobCount = jobs.Count,
                    Jobs = jobs.Select(j => new
                    {
                        j.JobId,
                        j.FileName,
                        State = j.State.ToString(),
                        j.TotalFrames,
                        j.ProcessedFrames,
                        j.TotalPersonsDetected
                    })
                });
            }
            catch (Exception ex)
            {
                return Ok(new
                {
                    ServiceAvailable = false,
                    Error = ex.Message,
                    StackTrace = ex.StackTrace
                });
            }
        }
    }
}