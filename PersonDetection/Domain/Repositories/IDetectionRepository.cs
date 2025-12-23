// PersonDetection.Domain/Repositories/IDetectionRepository.cs
namespace PersonDetection.Domain.Repositories
{
    using PersonDetection.Domain.Entities;

    public interface IDetectionRepository
    {
        Task<int> SaveAsync(DetectionResult result, CancellationToken ct = default);
        Task<List<DetectionResult>> GetRecentAsync(int cameraId, int count, CancellationToken ct = default);
        Task<List<DetectedPerson>> GetPersonHistoryAsync(Guid personId, CancellationToken ct = default);
        Task<Dictionary<int, int>> GetActiveCountsAsync(CancellationToken ct = default);
        Task<int> GetUniquePersonCountAsync(CancellationToken ct = default);
        Task<int> GetUniquePersonCountAsync(int cameraId, CancellationToken ct = default);
        Task<int> GetTodayDetectionCountAsync(int cameraId, CancellationToken ct = default);
    }

    public interface ICameraRepository
    {
        Task<CameraSession?> GetActiveSessionAsync(int cameraId, CancellationToken ct = default);
        Task<int> CreateSessionAsync(CameraSession session, CancellationToken ct = default);
        Task UpdateSessionAsync(CameraSession session, CancellationToken ct = default);
        Task<List<CameraSession>> GetAllActiveAsync(CancellationToken ct = default);
    }

    public interface IUnitOfWork : IDisposable
    {
        IDetectionRepository Detections { get; }
        ICameraRepository Cameras { get; }
        Task<int> SaveChangesAsync(CancellationToken ct = default);
    }
}