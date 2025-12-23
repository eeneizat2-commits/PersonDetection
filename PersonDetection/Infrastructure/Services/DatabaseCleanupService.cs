// PersonDetection.Infrastructure/Services/DatabaseCleanupService.cs
namespace PersonDetection.Infrastructure.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Infrastructure.Context;

    public class DatabaseCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PersistenceSettings _settings;
        private readonly ILogger<DatabaseCleanupService> _logger;

        public DatabaseCleanupService(
            IServiceProvider serviceProvider,
            IOptions<PersistenceSettings> settings,
            ILogger<DatabaseCleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Database cleanup service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_settings.CleanupIntervalMinutes), stoppingToken);
                    await CleanupOldRecords(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in database cleanup service");
                }
            }

            _logger.LogInformation("Database cleanup service stopped");
        }

        private async Task CleanupOldRecords(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            // Keep only last N results per camera
            var cameras = await context.DetectionResults
                .Select(d => d.CameraId)
                .Distinct()
                .ToListAsync(ct);

            int totalDeleted = 0;

            foreach (var cameraId in cameras)
            {
                var toDelete = await context.DetectionResults
                    .Where(d => d.CameraId == cameraId)
                    .OrderByDescending(d => d.Timestamp)
                    .Skip(_settings.MaxStoredResults)
                    .ToListAsync(ct);

                if (toDelete.Any())
                {
                    context.DetectionResults.RemoveRange(toDelete);
                    totalDeleted += toDelete.Count;
                }
            }

            if (totalDeleted > 0)
            {
                await context.SaveChangesAsync(ct);
                _logger.LogInformation("Cleaned up {Count} old detection records", totalDeleted);
            }
        }
    }
}