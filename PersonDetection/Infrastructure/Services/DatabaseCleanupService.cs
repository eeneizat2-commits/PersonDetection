namespace PersonDetection.Infrastructure.Services
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore.Migrations;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Domain.Services;
    using PersonDetection.Infrastructure.Context;
    using PersonDetection.Migrations;

    public class DatabaseCleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PersistenceSettings _settings;
        private readonly ILogger<DatabaseCleanupService> _logger;

        // ✅ NEW: Flag to let other services know cleanup is running
        private volatile bool _isRunningMaintenance = false;
        public bool IsRunningMaintenance => _isRunningMaintenance;

        private DateTime _lastDailyStatsUpdate = DateTime.MinValue;
        private DateTime _lastCleanupRun = DateTime.MinValue;
        private DateTime _lastFlush = DateTime.MinValue;

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
                    // ✅ NEW: Update DailyStats every 60 seconds (lightweight, no locks on other tables)
                    await UpdateDailyStatsIfNeeded(stoppingToken);

                    await FlushUnsavedPersonsIfNeeded(stoppingToken);

                    // Short sleep — DailyStats check runs frequently, cleanup runs on interval
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                    // Heavy cleanup only on the configured interval
                    if ((DateTime.UtcNow - _lastCleanupRun).TotalMinutes >= _settings.CleanupIntervalMinutes)
                    {
                        _isRunningMaintenance = true;

                        await CleanupOldRecords(stoppingToken);
                        await MaintainIndexes(stoppingToken);

                        _lastCleanupRun = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in database cleanup service");
                }
                finally
                {
                    _isRunningMaintenance = false;
                }
            }

            _logger.LogInformation("🧹 Database cleanup service stopped");
        }

        // ✅ NEW: Replaces Step 7 from sp_BatchSaveDetections
        // Runs ONCE every 60 seconds instead of 10 times every 10 seconds
        private async Task UpdateDailyStatsIfNeeded(CancellationToken ct)
        {
            if ((DateTime.UtcNow - _lastDailyStatsUpdate).TotalSeconds < 120)
                return;

            try
            {
                // ✅ Get count from PersonIdentityService (already in memory)
                var identityMatcher = _serviceProvider.GetService<IPersonIdentityMatcher>();
                var todayCount = identityMatcher?.GetTodayUniqueCount() ?? 0;

                if (todayCount <= 0)
                {
                    _lastDailyStatsUpdate = DateTime.UtcNow;
                    return;
                }

                // ✅ Only touch DailyStats table (10 rows — instant)
                var connString = "Server=DESKTOP-QML0799;Database=DetectionContext;Trusted_Connection=True;TrustServerCertificate=True;Connection Timeout=5;Command Timeout=5;Pooling=true;Max Pool Size=2;Application Name=DailyStats;";

                using var connection = new Microsoft.Data.SqlClient.SqlConnection(connString);
                await connection.OpenAsync(ct);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
            DECLARE @Now DATETIME2 = SYSUTCDATETIME();
            DECLARE @TodayDate DATE = CAST(@Now AS DATE);

            IF EXISTS (SELECT 1 FROM DailyStats WHERE [Date] = @TodayDate)
                UPDATE DailyStats 
                SET UniquePersonCount = @Count, LastUpdated = @Now
                WHERE [Date] = @TodayDate;
            ELSE
                INSERT INTO DailyStats ([Date], UniquePersonCount, LastUpdated)
                VALUES (@TodayDate, @Count, @Now);";
                cmd.CommandTimeout = 5;

                var param = cmd.CreateParameter();
                param.ParameterName = "@Count";
                param.Value = todayCount;
                cmd.Parameters.Add(param);

                await cmd.ExecuteNonQueryAsync(ct);

                _lastDailyStatsUpdate = DateTime.UtcNow;
                _logger.LogDebug("📊 DailyStats updated: {Count}", todayCount);
            }
            catch (OperationCanceledException) { /* ignore */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DailyStats update failed (non-critical)");
            }
        }

        private async Task FlushUnsavedPersonsIfNeeded(CancellationToken ct)
        {
            // ✅ Every 2 minutes, flush max 200 persons
            if ((DateTime.UtcNow - _lastFlush).TotalMinutes < 2)
                return;

            try
            {
                var matcher = _serviceProvider.GetService<IPersonIdentityMatcher>();
                if (matcher != null)
                {
                    await matcher.FlushUnsavedToDatabaseAsync(200);
                    _lastFlush = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Flush failed (non-critical)");
            }
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
                var idsToDelete = await context.DetectionResults
                    .Where(d => d.CameraId == cameraId)
                    .OrderByDescending(d => d.Timestamp)
                    .Skip(_settings.MaxStoredResults)
                    .Select(d => d.Id)
                    .ToListAsync(ct);

                if (idsToDelete.Count == 0) continue;

                const int batchSize = 500;
                for (int i = 0; i < idsToDelete.Count; i += batchSize)
                {
                    var batch = idsToDelete.Skip(i).Take(batchSize).ToList();

                    await context.DetectedPersons
                        .Where(dp => batch.Contains(dp.DetectionResultId))
                        .ExecuteDeleteAsync(ct);

                    await context.PersonSightings
                        .Where(ps => ps.DetectionResultId.HasValue
                            && batch.Contains(ps.DetectionResultId.Value))
                        .ExecuteDeleteAsync(ct);

                    await context.DetectionResults
                        .Where(d => batch.Contains(d.Id))
                        .ExecuteDeleteAsync(ct);

                    totalDeleted += batch.Count;

                    // ✅ NEW: Small delay between batches to reduce lock contention
                    await Task.Delay(100, ct);
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
                context.Database.SetCommandTimeout(600); // 10 min for ONLINE index ops

                var hour = DateTime.Now.Hour;
                if (hour >= 5 && hour < 24)
                {
                    _logger.LogDebug("🔧 Skipping index maintenance — not in window (0-5 AM)");
                    return;
                }

                var tables = new[]
                {
                    ("UniquePersons", "IX_UniquePersons_GlobalPersonId", 70),
                    ("UniquePersons", "IX_UniquePersons_LastSeenAt", 80),
                    ("DetectedPersons", "IX_DetectedPersons_GlobalPersonId", 80),
                    ("DetectedPersons", "IX_DetectedPersons_DetectedAt", 80),
                    ("PersonSightings", "IX_PersonSightings_UniquePersonId", 80),
                    ("PersonSightings", "IX_PersonSightings_SeenAt", 80),
                    ("DetectionResults", "IX_DetectionResults_Timestamp", 80),
                    ("UniquePersonFeatures", "IX_UniquePersonFeatures_GlobalPersonId", 80),
                    
                };

                int rebuilt = 0;

                foreach (var (table, indexName, fillFactor) in tables)
                {
                    try
                    {
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

                            // ✅ FIX: Try ONLINE rebuild first (doesn't lock table)
                            bool rebuilt_online = false;
                            try
                            {
                                await context.Database.ExecuteSqlRawAsync(
                                    $"ALTER INDEX [{indexName}] ON [{table}] REBUILD WITH (ONLINE = ON, FILLFACTOR = {fillFactor})",
                                    ct);
                                rebuilt_online = true;
                            }
                            catch
                            {
                                // ONLINE rebuild not supported on this SQL edition
                                // Fall back to REORGANIZE (never locks table)
                                _logger.LogDebug(
                                    "ONLINE rebuild not supported for {Index}, using REORGANIZE",
                                    indexName);
                            }

                            if (!rebuilt_online)
                            {
                                if (fragResult > 50)
                                {
                                    // ✅ High fragmentation: REBUILD with short lock
                                    // Use MAXDOP 1 to minimize lock duration
                                    await context.Database.ExecuteSqlRawAsync(
                                        $"ALTER INDEX [{indexName}] ON [{table}] REBUILD WITH (FILLFACTOR = {fillFactor}, MAXDOP = 1)",
                                        ct);
                                }
                                else
                                {
                                    // ✅ Medium fragmentation: REORGANIZE (no lock!)
                                    await context.Database.ExecuteSqlRawAsync(
                                        $"ALTER INDEX [{indexName}] ON [{table}] REORGANIZE",
                                        ct);
                                }
                            }

                            rebuilt++;

                            _logger.LogInformation(
                                "✅ Rebuilt {Index} on {Table} (online={Online})",
                                indexName, table, rebuilt_online);

                            // ✅ NEW: Delay between index ops to reduce contention
                            await Task.Delay(2000, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(
                            "Skipped index {Index} on {Table}: {Error}",
                            indexName, table, ex.Message);
                    }
                }

                if (rebuilt > 0)
                {
                    // ✅ FIX: Update statistics ONE TABLE AT A TIME with delays
                    var statsTables = new[] { "UniquePersons", "DetectedPersons",
                                              "PersonSightings", "DetectionResults" };

                    foreach (var table in statsTables)
                    {
                        try
                        {
                            await context.Database.ExecuteSqlRawAsync(
                                $"UPDATE STATISTICS [{table}]", ct);
                            await Task.Delay(1000, ct); // ✅ Delay between each
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("Failed to update stats for {Table}: {Error}",
                                table, ex.Message);
                        }
                    }

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