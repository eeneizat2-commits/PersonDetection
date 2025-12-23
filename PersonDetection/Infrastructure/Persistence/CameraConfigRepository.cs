// PersonDetection.Infrastructure/Persistence/CameraConfigRepository.cs
namespace PersonDetection.Infrastructure.Persistence
{
    using Microsoft.EntityFrameworkCore;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Repositories;
    using PersonDetection.Infrastructure.Context;

    public class CameraConfigRepository : ICameraConfigRepository
    {
        private readonly DetectionContext _context;

        public CameraConfigRepository(DetectionContext context)
        {
            _context = context;
        }

        public async Task<List<Camera>> GetAllAsync(CancellationToken ct = default)
        {
            return await _context.Cameras
                .OrderBy(c => c.DisplayOrder)
                .ThenBy(c => c.Name)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        public async Task<List<Camera>> GetEnabledAsync(CancellationToken ct = default)
        {
            return await _context.Cameras
                .Where(c => c.IsEnabled)
                .OrderBy(c => c.DisplayOrder)
                .ThenBy(c => c.Name)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        public async Task<Camera?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await _context.Cameras
                .FirstOrDefaultAsync(c => c.Id == id, ct);
        }

        public async Task<int> CreateAsync(Camera camera, CancellationToken ct = default)
        {
            _context.Cameras.Add(camera);
            await _context.SaveChangesAsync(ct);
            return camera.Id;
        }

        public async Task UpdateAsync(Camera camera, CancellationToken ct = default)
        {
            _context.Cameras.Update(camera);
            await _context.SaveChangesAsync(ct);
        }

        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            var camera = await _context.Cameras.FindAsync(new object[] { id }, ct);
            if (camera != null)
            {
                _context.Cameras.Remove(camera);
                await _context.SaveChangesAsync(ct);
            }
        }
    }
}