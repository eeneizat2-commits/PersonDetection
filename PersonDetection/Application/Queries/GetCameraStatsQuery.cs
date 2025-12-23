// PersonDetection.Application/Queries/GetCameraStatsQuery.cs
namespace PersonDetection.Application.Queries
{
    using PersonDetection.Application.Common;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Domain.Repositories;

    public record GetCameraStatsQuery(int CameraId, int RecentCount = 100) : IQuery<CameraStatsDto>;

    public class GetCameraStatsHandler : IQueryHandler<GetCameraStatsQuery, CameraStatsDto>
    {
        private readonly IUnitOfWork _uow;

        public GetCameraStatsHandler(IUnitOfWork uow)
        {
            _uow = uow;
        }

        public async Task<CameraStatsDto> Handle(GetCameraStatsQuery query, CancellationToken ct)
        {
            var recent = await _uow.Detections.GetRecentAsync(query.CameraId, query.RecentCount, ct);
            var uniqueCount = await _uow.Detections.GetUniquePersonCountAsync(query.CameraId, ct);
            var totalToday = await _uow.Detections.GetTodayDetectionCountAsync(query.CameraId, ct);

            var currentCount = recent.FirstOrDefault()?.ValidDetections ?? 0;

            var recentDtos = recent.Select(r => new DetectionResultDto(
                r.Id,
                r.CameraId,
                r.Timestamp,
                r.TotalDetections,
                r.ValidDetections,
                new List<PersonDetectionDto>()
            )).ToList();

            return new CameraStatsDto(
                query.CameraId,
                currentCount,
                uniqueCount,
                totalToday,
                recentDtos
            );
        }
    }
}