// PersonDetection.Application/Queries/GetHistoricalStatsQuery.cs
namespace PersonDetection.Application.Queries
{
    using Microsoft.Data.SqlClient;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using PersonDetection.Application.Common;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Infrastructure.Context;
    using System.Data;

    public class GetHistoricalStatsQuery : IQuery<HistoricalStatsDto>
    {
        public int? LastDays { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? CameraId { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
    }

    public class GetHistoricalStatsHandler : IQueryHandler<GetHistoricalStatsQuery, HistoricalStatsDto>
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GetHistoricalStatsHandler> _logger;
        private const int CommandTimeoutSeconds = 120;

        public GetHistoricalStatsHandler(
            IServiceProvider serviceProvider,
            ILogger<GetHistoricalStatsHandler> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<HistoricalStatsDto> Handle(
            GetHistoricalStatsQuery query, CancellationToken ct = default)
        {
            DateTime startDateTime;
            DateTime endDateTime;
            var today = DateTime.Today;

            if (query.StartDate.HasValue && query.EndDate.HasValue)
            {
                startDateTime = query.StartDate.Value.Date;
                endDateTime = query.EndDate.Value.Date;

                if (query.StartTime.HasValue)
                    startDateTime = startDateTime.Add(query.StartTime.Value);

                if (query.EndTime.HasValue)
                    endDateTime = endDateTime.Add(query.EndTime.Value);
                else
                    endDateTime = endDateTime.AddDays(1).AddTicks(-1);
            }
            else if (query.LastDays.HasValue && query.LastDays.Value > 0)
            {
                endDateTime = today.AddDays(1).AddTicks(-1);
                startDateTime = today.AddDays(-(query.LastDays.Value - 1));
            }
            else
            {
                endDateTime = today.AddDays(1).AddTicks(-1);
                startDateTime = today.AddDays(-6);
            }

            _logger.LogInformation(
                "📊 Stats: [{Start:yyyy-MM-dd HH:mm}] to [{End:yyyy-MM-dd HH:mm}], Camera: {Camera}",
                startDateTime, endDateTime, query.CameraId?.ToString() ?? "All");

            try
            {
                // ✅ FIX: Check cancellation before doing any work
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Stats query cancelled before execution");
                    return CreateEmptyResult(startDateTime, endDateTime);
                }

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                var connection = context.Database.GetDbConnection();
                if (connection.State != ConnectionState.Open)
                    await connection.OpenAsync(ct);

                // 1. Summary
                var summary = await GetSummaryAsync(
                    connection, startDateTime, endDateTime, query.CameraId, ct);

                // ✅ FIX: Check between expensive operations
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Stats cancelled after summary, skipping remaining queries");
                    return new HistoricalStatsDto
                    {
                        StartDate = summary.StartDate,
                        EndDate = summary.EndDate,
                        TotalDays = summary.TotalDays,
                        TotalUniquePersons = summary.TotalUniquePersons,
                        TotalDetections = summary.TotalDetections,
                        DailyStats = new List<DailyStatsDto>(),
                        CameraBreakdown = new List<CameraBreakdownDto>()
                    };
                }

                // 2. Daily Stats
                var dailyStats = await GetDailyStatsAsync(
                    connection, startDateTime, endDateTime, query.CameraId, ct);

                // ✅ FIX: Check before third query
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Stats cancelled after daily stats, skipping camera breakdown");
                    return new HistoricalStatsDto
                    {
                        StartDate = summary.StartDate,
                        EndDate = summary.EndDate,
                        TotalDays = summary.TotalDays,
                        TotalUniquePersons = summary.TotalUniquePersons,
                        TotalDetections = summary.TotalDetections,
                        DailyStats = dailyStats,
                        CameraBreakdown = new List<CameraBreakdownDto>()
                    };
                }

                // 3. Camera Breakdown
                var cameraBreakdown = await GetCameraBreakdownAsync(
                    connection, startDateTime, endDateTime, ct);

                var result = new HistoricalStatsDto
                {
                    StartDate = summary.StartDate,
                    EndDate = summary.EndDate,
                    TotalDays = summary.TotalDays,
                    TotalUniquePersons = summary.TotalUniquePersons,
                    TotalDetections = summary.TotalDetections,
                    DailyStats = dailyStats,
                    CameraBreakdown = cameraBreakdown
                };

                _logger.LogInformation(
                    "📊 Stats result: {Unique} unique, {Detections} detections, {Days} days, {DailyCount} daily rows",
                    result.TotalUniquePersons, result.TotalDetections,
                    result.TotalDays, result.DailyStats.Count);

                return result;
            }
            // ✅ FIX: Catch cancellation BEFORE the generic Exception catch
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "📊 Stats query cancelled for range {Start} - {End} (user changed date or navigated away)",
                    startDateTime, endDateTime);
                return CreateEmptyResult(startDateTime, endDateTime);
            }
            catch (SqlException ex) when (ct.IsCancellationRequested)
            {
                // SQL Server throws SqlException (not OCE) when a running command is aborted
                _logger.LogInformation(
                    "📊 Stats SQL command cancelled: {Message}", ex.Message);
                return CreateEmptyResult(startDateTime, endDateTime);
            }
            catch (SqlException ex) when (ex.Number == -2)
            {
                _logger.LogError(ex, "SQL timeout while fetching stats");
                return CreateEmptyResult(startDateTime, endDateTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching historical stats");
                throw;
            }
        }

        // ═══════════════════════════════════════════════════════
        // SP 1: Summary (already fixed — no changes needed)
        // ═══════════════════════════════════════════════════════
        private async Task<SpStatsSummary> GetSummaryAsync(
            System.Data.Common.DbConnection connection,
            DateTime startDate, DateTime endDate, int? cameraId,
            CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "sp_GetStatsSummary";
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = CommandTimeoutSeconds;

                cmd.Parameters.Add(new SqlParameter("@StartDate", startDate));
                cmd.Parameters.Add(new SqlParameter("@EndDate", endDate));
                cmd.Parameters.Add(new SqlParameter("@CameraId", (object?)cameraId ?? DBNull.Value));

                using var reader = await cmd.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    return new SpStatsSummary
                    {
                        StartDate = reader.GetDateTime(reader.GetOrdinal("StartDate")),
                        EndDate = reader.GetDateTime(reader.GetOrdinal("EndDate")),
                        TotalDays = reader.GetInt32(reader.GetOrdinal("TotalDays")),
                        TotalUniquePersons = reader.GetInt32(reader.GetOrdinal("TotalUniquePersons")),
                        TotalDetections = reader.GetInt32(reader.GetOrdinal("TotalDetections"))
                    };
                }

                _logger.LogWarning("sp_GetStatsSummary returned no rows");
                return GetDefaultSummary(startDate, endDate);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("GetSummaryAsync was cancelled");
                return GetDefaultSummary(startDate, endDate);
            }
            catch (SqlException ex) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("GetSummaryAsync SQL command was cancelled: {Message}", ex.Message);
                return GetDefaultSummary(startDate, endDate);
            }
        }

        private SpStatsSummary GetDefaultSummary(DateTime startDate, DateTime endDate)
        {
            return new SpStatsSummary
            {
                StartDate = startDate,
                EndDate = endDate,
                TotalDays = 0,
                TotalUniquePersons = 0,
                TotalDetections = 0
            };
        }

        // ═══════════════════════════════════════════════════════
        // SP 2: Daily Stats (already fixed — no changes needed)
        // ═══════════════════════════════════════════════════════
        private async Task<List<DailyStatsDto>> GetDailyStatsAsync(
            System.Data.Common.DbConnection connection,
            DateTime startDate, DateTime endDate, int? cameraId,
            CancellationToken ct)
        {
            var results = new List<DailyStatsDto>();

            try
            {
                ct.ThrowIfCancellationRequested();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "sp_GetDailyStats";
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = CommandTimeoutSeconds;

                cmd.Parameters.Add(new SqlParameter("@StartDate", startDate));
                cmd.Parameters.Add(new SqlParameter("@EndDate", endDate));
                cmd.Parameters.Add(new SqlParameter("@CameraId", (object?)cameraId ?? DBNull.Value));

                using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    if (ct.IsCancellationRequested)
                    {
                        _logger.LogInformation("GetDailyStatsAsync cancelled while reading rows");
                        return results;
                    }

                    results.Add(new DailyStatsDto
                    {
                        Date = reader.GetDateTime(reader.GetOrdinal("Date")),
                        DayName = reader.GetString(reader.GetOrdinal("DayName")),
                        UniquePersons = reader.GetInt32(reader.GetOrdinal("UniquePersons")),
                        TotalDetections = reader.GetInt32(reader.GetOrdinal("TotalDetections")),
                        PeakHour = reader.GetInt32(reader.GetOrdinal("PeakHour")),
                        PeakHourCount = reader.GetInt32(reader.GetOrdinal("PeakHourCount"))
                    });
                }
            }
            catch (SqlException ex) when (ex.Number == 530)
            {
                _logger.LogWarning("sp_GetDailyStats hit MAXRECURSION limit, using fallback");
                results = await GetDailyStatsFallbackAsync(connection, startDate, endDate, cameraId, ct);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("GetDailyStatsAsync was cancelled");
                return new List<DailyStatsDto>();
            }
            catch (SqlException ex) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("GetDailyStatsAsync SQL cancelled: {Message}", ex.Message);
                return new List<DailyStatsDto>();
            }

            return results;
        }

        private async Task<List<DailyStatsDto>> GetDailyStatsFallbackAsync(
            System.Data.Common.DbConnection connection,
            DateTime startDate, DateTime endDate, int? cameraId,
            CancellationToken ct)
        {
            var results = new List<DailyStatsDto>();
            var currentDate = startDate.Date;
            var endDateOnly = endDate.Date;

            while (currentDate <= endDateOnly)
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "GetDailyStatsFallbackAsync cancelled at {Date}, returning {Count} partial results",
                        currentDate, results.Count);
                    return results;
                }

                try
                {
                    var dayStart = currentDate;
                    var dayEnd = currentDate.AddDays(1).AddTicks(-1);

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
                        SELECT 
                            @Date AS [Date],
                            DATENAME(WEEKDAY, @Date) AS DayName,
                            (SELECT COUNT(*) FROM UniquePersons 
                             WHERE IsActive = 1 
                               AND FirstSeenAt >= @DayStart AND FirstSeenAt <= @DayEnd
                               AND (@CameraId IS NULL OR FirstSeenCameraId = @CameraId)
                            ) AS UniquePersons,
                            (SELECT ISNULL(SUM(TotalDetections), 0) FROM DetectionResults 
                             WHERE Timestamp >= @DayStart AND Timestamp <= @DayEnd
                               AND (@CameraId IS NULL OR CameraId = @CameraId)
                            ) AS TotalDetections,
                            0 AS PeakHour,
                            0 AS PeakHourCount";
                    cmd.CommandType = CommandType.Text;
                    cmd.CommandTimeout = CommandTimeoutSeconds;

                    cmd.Parameters.Add(new SqlParameter("@Date", currentDate));
                    cmd.Parameters.Add(new SqlParameter("@DayStart", dayStart));
                    cmd.Parameters.Add(new SqlParameter("@DayEnd", dayEnd));
                    cmd.Parameters.Add(new SqlParameter("@CameraId", (object?)cameraId ?? DBNull.Value));

                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        results.Add(new DailyStatsDto
                        {
                            Date = reader.GetDateTime(0),
                            DayName = reader.GetString(1),
                            UniquePersons = reader.GetInt32(2),
                            TotalDetections = reader.GetInt32(3),
                            PeakHour = reader.GetInt32(4),
                            PeakHourCount = reader.GetInt32(5)
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation(
                        "GetDailyStatsFallbackAsync cancelled during query for {Date}", currentDate);
                    return results;
                }
                catch (SqlException ex) when (ct.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "GetDailyStatsFallbackAsync SQL cancelled at {Date}: {Message}",
                        currentDate, ex.Message);
                    return results;
                }

                currentDate = currentDate.AddDays(1);
            }

            return results;
        }

        // ═══════════════════════════════════════════════════════
        // SP 3: Camera Breakdown — ✅ FIX: Added cancellation handling
        // ═══════════════════════════════════════════════════════
        private async Task<List<CameraBreakdownDto>> GetCameraBreakdownAsync(
            System.Data.Common.DbConnection connection,
            DateTime startDate, DateTime endDate,
            CancellationToken ct)
        {
            var results = new List<CameraBreakdownDto>();

            try
            {
                ct.ThrowIfCancellationRequested();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "sp_GetCameraBreakdown";
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandTimeout = CommandTimeoutSeconds;

                cmd.Parameters.Add(new SqlParameter("@StartDate", startDate));
                cmd.Parameters.Add(new SqlParameter("@EndDate", endDate));

                using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
                    if (ct.IsCancellationRequested)
                    {
                        _logger.LogInformation("GetCameraBreakdownAsync cancelled while reading rows");
                        return results;
                    }

                    results.Add(new CameraBreakdownDto
                    {
                        CameraId = reader.GetInt32(reader.GetOrdinal("CameraId")),
                        CameraName = reader.GetString(reader.GetOrdinal("CameraName")),
                        TotalDetections = reader.GetInt32(reader.GetOrdinal("TotalDetections")),
                        UniquePersons = reader.GetInt32(reader.GetOrdinal("UniquePersons"))
                    });
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("GetCameraBreakdownAsync was cancelled");
                return new List<CameraBreakdownDto>();
            }
            catch (SqlException ex) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("GetCameraBreakdownAsync SQL cancelled: {Message}", ex.Message);
                return new List<CameraBreakdownDto>();
            }

            return results;
        }

        private HistoricalStatsDto CreateEmptyResult(DateTime startDate, DateTime endDate)
        {
            return new HistoricalStatsDto
            {
                StartDate = startDate,
                EndDate = endDate,
                TotalDays = 0,
                TotalUniquePersons = 0,
                TotalDetections = 0,
                DailyStats = new List<DailyStatsDto>(),
                CameraBreakdown = new List<CameraBreakdownDto>()
            };
        }
    }
}