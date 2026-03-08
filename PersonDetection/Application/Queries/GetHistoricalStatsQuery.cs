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

        public async Task<HistoricalStatsDto> Handle(GetHistoricalStatsQuery query, CancellationToken ct = default)
        {
            // Calculate date range
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
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                var connection = context.Database.GetDbConnection();
                if (connection.State != ConnectionState.Open)
                    await connection.OpenAsync(ct);

                // ═══════════════════════════════════════════════════════
                // 1. Get Summary via raw ADO.NET
                // ═══════════════════════════════════════════════════════
                var summary = await GetSummaryAsync(connection, startDateTime, endDateTime, query.CameraId, ct);

                // ═══════════════════════════════════════════════════════
                // 2. Get Daily Stats via raw ADO.NET
                // ═══════════════════════════════════════════════════════
                var dailyStats = await GetDailyStatsAsync(connection, startDateTime, endDateTime, query.CameraId, ct);

                // ═══════════════════════════════════════════════════════
                // 3. Get Camera Breakdown via raw ADO.NET
                // ═══════════════════════════════════════════════════════
                var cameraBreakdown = await GetCameraBreakdownAsync(connection, startDateTime, endDateTime, ct);

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
                    result.TotalUniquePersons, result.TotalDetections, result.TotalDays, result.DailyStats.Count);

                return result;
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

        // ═══════════════════════════════════════════════════════════════
        // SP 1: Summary
        // ═══════════════════════════════════════════════════════════════
        private async Task<SpStatsSummary> GetSummaryAsync(
            System.Data.Common.DbConnection connection,
            DateTime startDate, DateTime endDate, int? cameraId,
            CancellationToken ct)
        {
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
            return new SpStatsSummary
            {
                StartDate = startDate,
                EndDate = endDate,
                TotalDays = 0,
                TotalUniquePersons = 0,
                TotalDetections = 0
            };
        }

        // ═══════════════════════════════════════════════════════════════
        // SP 2: Daily Stats
        // ═══════════════════════════════════════════════════════════════
        private async Task<List<DailyStatsDto>> GetDailyStatsAsync(
            System.Data.Common.DbConnection connection,
            DateTime startDate, DateTime endDate, int? cameraId,
            CancellationToken ct)
        {
            var results = new List<DailyStatsDto>();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "sp_GetDailyStats";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandTimeout = CommandTimeoutSeconds;

            cmd.Parameters.Add(new SqlParameter("@StartDate", startDate));
            cmd.Parameters.Add(new SqlParameter("@EndDate", endDate));
            cmd.Parameters.Add(new SqlParameter("@CameraId", (object?)cameraId ?? DBNull.Value));

            try
            {
                using var reader = await cmd.ExecuteReaderAsync(ct);

                while (await reader.ReadAsync(ct))
                {
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
                // MAXRECURSION error — fallback: generate dates in C# and query individually
                _logger.LogWarning("sp_GetDailyStats hit MAXRECURSION limit, using fallback");
                results = await GetDailyStatsFallbackAsync(connection, startDate, endDate, cameraId, ct);
            }

            return results;
        }

        /// <summary>
        /// Fallback when CTE recursion limit is hit — query day by day
        /// </summary>
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

                currentDate = currentDate.AddDays(1);
            }

            return results;
        }

        // ═══════════════════════════════════════════════════════════════
        // SP 3: Camera Breakdown
        // ═══════════════════════════════════════════════════════════════
        private async Task<List<CameraBreakdownDto>> GetCameraBreakdownAsync(
            System.Data.Common.DbConnection connection,
            DateTime startDate, DateTime endDate,
            CancellationToken ct)
        {
            var results = new List<CameraBreakdownDto>();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "sp_GetCameraBreakdown";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandTimeout = CommandTimeoutSeconds;

            cmd.Parameters.Add(new SqlParameter("@StartDate", startDate));
            cmd.Parameters.Add(new SqlParameter("@EndDate", endDate));

            using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                results.Add(new CameraBreakdownDto
                {
                    CameraId = reader.GetInt32(reader.GetOrdinal("CameraId")),
                    CameraName = reader.GetString(reader.GetOrdinal("CameraName")),
                    TotalDetections = reader.GetInt32(reader.GetOrdinal("TotalDetections")),
                    UniquePersons = reader.GetInt32(reader.GetOrdinal("UniquePersons"))
                });
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