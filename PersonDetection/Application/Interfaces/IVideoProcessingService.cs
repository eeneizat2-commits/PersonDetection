// PersonDetection.Application/Interfaces/IVideoProcessingService.cs
namespace PersonDetection.Application.Interfaces
{
    using PersonDetection.Application.DTOs;

    public interface IVideoProcessingService
    {
        Task QueueVideoAsync(Guid jobId, string filePath, string fileName, int frameSkip, bool extractFeatures, CancellationToken ct);
        VideoProcessingStatusDto? GetStatus(Guid jobId);
        VideoProcessingSummaryDto? GetSummary(Guid jobId);
        IEnumerable<VideoProcessingStatusDto> GetAllJobs();
        bool CancelJob(Guid jobId);
        void CleanupJob(Guid jobId);
    }
}