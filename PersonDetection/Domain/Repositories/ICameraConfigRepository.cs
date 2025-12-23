// PersonDetection.Domain/Repositories/ICameraConfigRepository.cs
namespace PersonDetection.Domain.Repositories
{
    using PersonDetection.Domain.Entities;

    public interface ICameraConfigRepository
    {
        Task<List<Camera>> GetAllAsync(CancellationToken ct = default);
        Task<List<Camera>> GetEnabledAsync(CancellationToken ct = default);
        Task<Camera?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<int> CreateAsync(Camera camera, CancellationToken ct = default);
        Task UpdateAsync(Camera camera, CancellationToken ct = default);
        Task DeleteAsync(int id, CancellationToken ct = default);
    }
}