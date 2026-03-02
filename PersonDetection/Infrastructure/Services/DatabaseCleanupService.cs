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
            _logger.LogInformation("🧹 Database cleanup service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMinutes(_settings.CleanupIntervalMinutes),
                        stoppingToken);

                    await CleanupOldRecords(stoppingToken);
                    await MaintainIndexes(stoppingToken);
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

            _logger.LogInformation("🧹 Database cleanup service stopped");
        }

        private async Task CleanupOldRecords(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();
            context.Database.SetCommandTimeout(120);

            var cameras = await context.DetectionResults
                .Select(d => d.CameraId)
                .Distinct()
                .ToListAsync(ct);

            int totalDeleted = 0;

            foreach (var cameraId in cameras)
            {
                // Delete in batches to avoid timeout
                var idsToDelete = await context.DetectionResults
                    .Where(d => d.CameraId == cameraId)
                    .OrderByDescending(d => d.Timestamp)
                    .Skip(_settings.MaxStoredResults)
                    .Select(d => d.Id)
                    .ToListAsync(ct);

                if (idsToDelete.Count == 0) continue;

                // Batch delete — 500 at a time
                const int batchSize = 500;
                for (int i = 0; i < idsToDelete.Count; i += batchSize)
                {
                    var batch = idsToDelete.Skip(i).Take(batchSize).ToList();

                    // Delete child records first (DetectedPersons)
                    await context.DetectedPersons
                        .Where(dp => batch.Contains(dp.DetectionResultId))
                        .ExecuteDeleteAsync(ct);

                    // Delete child records (PersonSightings)
                    await context.PersonSightings
                        .Where(ps => ps.DetectionResultId.HasValue
                            && batch.Contains(ps.DetectionResultId.Value))
                        .ExecuteDeleteAsync(ct);

                    // Delete parent records
                    await context.DetectionResults
                        .Where(d => batch.Contains(d.Id))
                        .ExecuteDeleteAsync(ct);

                    totalDeleted += batch.Count;
                }
            }

            if (totalDeleted > 0)
            {
                _logger.LogInformation(
                    "🧹 Cleaned up {Count} old detection records", totalDeleted);
            }
        }

        private async Task MaintainIndexes(CancellationToken ct)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();
                context.Database.SetCommandTimeout(300); // 5 min for index ops

                // Check fragmentation on critical tables
                var tables = new[]
                {
                    ("UniquePersons", "IX_UniquePersons_GlobalPersonId", 70),
                    ("UniquePersons", "IX_UniquePersons_LastSeenAt", 80),
                    ("DetectedPersons", "IX_DetectedPersons_GlobalPersonId", 80),
                    ("DetectedPersons", "IX_DetectedPersons_DetectedAt", 80),
                    ("PersonSightings", "IX_PersonSightings_UniquePersonId", 80),
                    ("PersonSightings", "IX_PersonSightings_SeenAt", 80),
                    ("DetectionResults", "IX_DetectionResults_Timestamp", 80),
                };

                int rebuilt = 0;

                foreach (var (table, indexName, fillFactor) in tables)
                {
                    try
                    {
                        // Check fragmentation
                        var fragResult = await context.Database
                            .SqlQueryRaw<decimal>(
                                @"SELECT CAST(ISNULL(avg_fragmentation_in_percent, 0) AS decimal(10,2)) AS [Value]
                                  FROM sys.dm_db_index_physical_stats(
                                      DB_ID(), OBJECT_ID({0}), NULL, NULL, 'LIMITED') ips
                                  JOIN sys.indexes i 
                                      ON ips.object_id = i.object_id 
                                      AND ips.index_id = i.index_id
                                  WHERE i.name = {1}",
                                table, indexName)
                            .FirstOrDefaultAsync(ct);

                        if (fragResult > 30)
                        {
                            _logger.LogInformation(
                                "🔧 Rebuilding {Index} on {Table} (fragmentation: {Frag:F1}%)",
                                indexName, table, fragResult);

                            await context.Database.ExecuteSqlRawAsync(
                                $"ALTER INDEX [{indexName}] ON [{table}] REBUILD WITH (FILLFACTOR = {fillFactor})",
                                ct);

                            rebuilt++;

                            _logger.LogInformation(
                                "✅ Rebuilt {Index} on {Table}", indexName, table);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Index might not exist — that's OK, skip it
                        _logger.LogDebug(
                            "Skipped index {Index} on {Table}: {Error}",
                            indexName, table, ex.Message);
                    }
                }

                if (rebuilt > 0)
                {
                    // Update statistics after rebuilds
                    await context.Database.ExecuteSqlRawAsync(
                        "UPDATE STATISTICS UniquePersons; " +
                        "UPDATE STATISTICS DetectedPersons; " +
                        "UPDATE STATISTICS PersonSightings; " +
                        "UPDATE STATISTICS DetectionResults;", ct);

                    _logger.LogInformation("🔧 Rebuilt {Count} fragmented indexes", rebuilt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Index maintenance failed (non-critical)");
            }
        }
    }
}