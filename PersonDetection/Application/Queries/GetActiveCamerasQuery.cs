// PersonDetection.Application/Queries/GetActiveCamerasQuery.cs
namespace PersonDetection.Application.Queries
{
    using PersonDetection.Application.Common;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Domain.Repositories;

    public record GetActiveCamerasQuery : IQuery<ActiveCamerasDto>;

    public class GetActiveCamerasHandler : IQueryHandler<GetActiveCamerasQuery, ActiveCamerasDto>
    {
        private readonly IUnitOfWork _uow;

        public GetActiveCamerasHandler(IUnitOfWork uow)
        {
            _uow = uow;
        }

        public async Task<ActiveCamerasDto> Handle(GetActiveCamerasQuery query, CancellationToken ct)
        {
            var activeSessions = await _uow.Cameras.GetAllActiveAsync(ct);
            var counts = await _uow.Detections.GetActiveCountsAsync(ct);
            var totalPersons = counts.Values.Sum();
            var uniquePersons = await _uow.Detections.GetUniquePersonCountAsync(ct);

            return new ActiveCamerasDto(
                activeSessions.Count,
                totalPersons,
                uniquePersons,
                counts
            );
        }
    }
}