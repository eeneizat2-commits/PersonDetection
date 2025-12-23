// PersonDetection.Domain/Entities/VideoJob.cs
namespace PersonDetection.Domain.Entities
{
    using PersonDetection.Domain.Common;

    public class VideoJob : Entity
    {
        public Guid JobId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string? StoredFilePath { get; set; }  // 👈 Path to saved video in uploads folder
        public string? OriginalFilePath { get; set; }
        public string? VideoDataBase64 { get; set; }  // Store video as base64
        public VideoJobState State { get; set; } = VideoJobState.Queued;
        public int TotalFrames { get; set; }
        public int ProcessedFrames { get; set; }
        public int TotalDetections { get; set; }
        public int UniquePersonCount { get; set; }
        public double VideoDurationSeconds { get; set; }
        public double VideoFps { get; set; }
        public double ProcessingTimeSeconds { get; set; }
        public int FrameSkip { get; set; } = 5;
        public double? AveragePersonsPerFrame { get; set; }  // 👈 ADD THIS
        public int? PeakPersonCount { get; set; }            // 👈 ADD THIS
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }

        // Navigation properties
        public ICollection<VideoPersonTimeline> PersonTimelines { get; set; } = new List<VideoPersonTimeline>();
        public ICollection<DetectionResult> DetectionResults { get; set; } = new List<DetectionResult>();
        public ICollection<DetectedPerson> DetectedPersons { get; set; } = new List<DetectedPerson>();

        public VideoJob()
        {
            JobId = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
            StartedAt = DateTime.UtcNow;
        }

        public static VideoJob Create(Guid jobId, string fileName, string filePath, int frameSkip)
        {
            return new VideoJob
            {
                JobId = jobId,
                FileName = fileName,
                OriginalFilePath = filePath,
                FrameSkip = frameSkip,
                State = VideoJobState.Queued,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow
            };
        }

        public void SetVideoData(byte[] videoBytes)
        {
            VideoDataBase64 = Convert.ToBase64String(videoBytes);
        }

        public byte[]? GetVideoData()
        {
            if (string.IsNullOrEmpty(VideoDataBase64)) return null;
            return Convert.FromBase64String(VideoDataBase64);
        }

        public void MarkAsProcessing()
        {
            State = VideoJobState.Processing;
        }

        public void MarkAsCompleted(int totalFrames, int processedFrames, int totalDetections,
            int uniquePersonCount, double durationSeconds, double fps, double processingTime)
        {
            State = VideoJobState.Completed;
            TotalFrames = totalFrames;
            ProcessedFrames = processedFrames;
            TotalDetections = totalDetections;
            UniquePersonCount = uniquePersonCount;
            VideoDurationSeconds = durationSeconds;
            VideoFps = fps;
            ProcessingTimeSeconds = processingTime;
            CompletedAt = DateTime.UtcNow;
        }

        public void MarkAsFailed(string errorMessage)
        {
            State = VideoJobState.Failed;
            ErrorMessage = errorMessage;
            CompletedAt = DateTime.UtcNow;
        }

        public void MarkAsCancelled()
        {
            State = VideoJobState.Cancelled;
            CompletedAt = DateTime.UtcNow;
        }
    }

    public enum VideoJobState
    {
        Queued = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4
    }
}