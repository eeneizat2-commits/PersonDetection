// PersonDetection.Infrastructure/Persistence/DetectionRepository.cs
namespace PersonDetection.Infrastructure.Persistence
{
    using Microsoft.EntityFrameworkCore;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Repositories;
    using PersonDetection.Infrastructure.Context;

    public class DetectionRepository : IDetectionRepository
    {
        private readonly DetectionContext _context;

        public DetectionRepository(DetectionContext context)
        {
            _context = context;
        }

        public Task<int> SaveAsync(DetectionResult result, CancellationToken ct = default)
        {
            _context.DetectionResults.Add(result);
            return Task.FromResult(result.Id);
        }

        public async Task<List<DetectionResult>> GetRecentAsync(int cameraId, int count, CancellationToken ct = default)
        {
            return await _context.DetectionResults
                .Where(d => d.CameraId == cameraId)
                .OrderByDescending(d => d.Timestamp)
                .Take(count)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        public async Task<List<DetectedPerson>> GetPersonHistoryAsync(Guid personId, CancellationToken ct = default)
        {
            return await _context.DetectedPersons
                .Where(p => p.GlobalPersonId == personId)
                .OrderByDescending(p => p.DetectedAt)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        public async Task<Dictionary<int, int>> GetActiveCountsAsync(CancellationToken ct = default)
        {
            var recent = DateTime.UtcNow.AddMinutes(-5);

            var results = await _context.DetectionResults
                .Where(d => d.Timestamp >= recent)
                .GroupBy(d => d.CameraId)
                .Select(g => new
                {
                    CameraId = g.Key,
                    Count = g.OrderByDescending(x => x.Timestamp).First().ValidDetections
                })
                .ToListAsync(ct);

            return results.ToDictionary(x => x.CameraId, x => x.Count);
        }

        public async Task<int> GetUniquePersonCountAsync(CancellationToken ct = default)
        {
            var today = DateTime.UtcNow.Date;

            // Count from UniquePersons table
            return await _context.UniquePersons
                .Where(u => u.IsActive && u.FirstSeenAt >= today)
                .CountAsync(ct);
        }

        public async Task<int> GetUniquePersonCountAsync(int cameraId, CancellationToken ct = default)
        {
            var today = DateTime.UtcNow.Date;

            // Count unique persons seen on this camera today
            return await _context.PersonSightings
                .Where(s => s.CameraId == cameraId && s.SeenAt >= today)
                .Select(s => s.UniquePersonId)
                .Distinct()
                .CountAsync(ct);
        }

        public async Task<int> GetTodayDetectionCountAsync(int cameraId, CancellationToken ct = default)
        {
            var today = DateTime.UtcNow.Date;

            return await _context.DetectionResults
                .Where(d => d.CameraId == cameraId && d.Timestamp >= today)
                .SumAsync(d => d.ValidDetections, ct);
        }
    }

    public class CameraRepository : ICameraRepository
    {
        private readonly DetectionContext _context;

        public CameraRepository(DetectionContext context)
        {
            _context = context;
        }

        public async Task<CameraSession?> GetActiveSessionAsync(int cameraId, CancellationToken ct = default)
        {
            return await _context.CameraSessions
                .Where(c => c.CameraId == cameraId && c.IsActive)
                .FirstOrDefaultAsync(ct);
        }

        public Task<int> CreateSessionAsync(CameraSession session, CancellationToken ct = default)
        {
            _context.CameraSessions.Add(session);
            return Task.FromResult(session.Id);
        }

        public Task UpdateSessionAsync(CameraSession session, CancellationToken ct = default)
        {
            _context.CameraSessions.Update(session);
            return Task.CompletedTask;
        }

        public async Task<List<CameraSession>> GetAllActiveAsync(CancellationToken ct = default)
        {
            return await _context.CameraSessions
                .Where(c => c.IsActive)
                .AsNoTracking()
                .ToListAsync(ct);
        }
    }

    public class UnitOfWork : IUnitOfWork
    {
        private readonly DetectionContext _context;

        public IDetectionRepository Detections { get; }
        public ICameraRepository Cameras { get; }

        public UnitOfWork(DetectionContext context)
        {
            _context = context;
            Detections = new DetectionRepository(context);
            Cameras = new CameraRepository(context);
        }

        public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            return await _context.SaveChangesAsync(ct);
        }

        public void Dispose()
        {
            _context.Dispose();
        }
    }
}