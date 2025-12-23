namespace PersonDetection.Application.Queries
{
    using PersonDetection.Application.Common;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Application.Interfaces;

    public record GetVideoStatusQuery(Guid JobId) : IQuery<VideoProcessingStatusDto?>;

    public class GetVideoStatusHandler : IQueryHandler<GetVideoStatusQuery, VideoProcessingStatusDto?>
    {
        private readonly IVideoProcessingService _videoService;

        public GetVideoStatusHandler(IVideoProcessingService videoService)
        {
            _videoService = videoService;
        }

        public Task<VideoProcessingStatusDto?> Handle(GetVideoStatusQuery query, CancellationToken ct)
        {
            var status = _videoService.GetStatus(query.JobId);
            return Task.FromResult(status);
        }
    }

    public record GetVideoSummaryQuery(Guid JobId) : IQuery<VideoProcessingSummaryDto?>;

    public class GetVideoSummaryHandler : IQueryHandler<GetVideoSummaryQuery, VideoProcessingSummaryDto?>
    {
        private readonly IVideoProcessingService _videoService;

        public GetVideoSummaryHandler(IVideoProcessingService videoService)
        {
            _videoService = videoService;
        }

        public Task<VideoProcessingSummaryDto?> Handle(GetVideoSummaryQuery query, CancellationToken ct)
        {
            var summary = _videoService.GetSummary(query.JobId);
            return Task.FromResult(summary);
        }
    }

    public record GetAllVideoJobsQuery : IQuery<List<VideoProcessingStatusDto>>;

    public class GetAllVideoJobsHandler : IQueryHandler<GetAllVideoJobsQuery, List<VideoProcessingStatusDto>>
    {
        private readonly IVideoProcessingService _videoService;

        public GetAllVideoJobsHandler(IVideoProcessingService videoService)
        {
            _videoService = videoService;
        }

        public Task<List<VideoProcessingStatusDto>> Handle(GetAllVideoJobsQuery query, CancellationToken ct)
        {
            var jobs = _videoService.GetAllJobs().ToList();
            return Task.FromResult(jobs);
        }
    }
}