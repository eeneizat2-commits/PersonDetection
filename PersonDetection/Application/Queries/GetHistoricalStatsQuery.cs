using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PersonDetection.Application.Common;
using PersonDetection.Application.DTOs;
using PersonDetection.Infrastructure.Context;

namespace PersonDetection.Application.Queries
{
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

        // Configurable timeout (in seconds)
        private const int CommandTimeoutSeconds = 120; // 2 minutes

        public GetHistoricalStatsHandler(
            IServiceProvider serviceProvider,
            ILogger<GetHistoricalStatsHandler> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<HistoricalStatsDto> Handle(GetHistoricalStatsQuery query, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            // ═══════════════════════════════════════════════════════════════
            // SET COMMAND TIMEOUT
            // ═══════════════════════════════════════════════════════════════
            context.Database.SetCommandTimeout(TimeSpan.FromSeconds(CommandTimeoutSeconds));

            // Calculate date range
            var today = DateTime.Today;
            DateTime startDateTime;
            DateTime endDateTime;

            if (query.StartDate.HasValue && query.EndDate.HasValue)
            {
                startDateTime = query.StartDate.Value.Date;
                endDateTime = query.EndDate.Value.Date;

                if (query.StartTime.HasValue)
                {
                    startDateTime = startDateTime.Add(query.StartTime.Value);
                }

                if (query.EndTime.HasValue)
                {
                    endDateTime = endDateTime.Add(query.EndTime.Value);
                }
                else
                {
                    endDateTime = endDateTime.AddDays(1).AddTicks(-1);
                }
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
                "📊 SP Stats: [{Start:yyyy-MM-dd HH:mm}] to [{End:yyyy-MM-dd HH:mm}], Camera: {Camera}",
                startDateTime, endDateTime, query.CameraId?.ToString() ?? "All");

            try
            {
                // ═══════════════════════════════════════════════════════════
                // Call Stored Procedures with error handling
                // ═══════════════════════════════════════════════════════════

                // 1. Get Summary
                var summaryList = await context.SpStatsSummary
                    .FromSqlInterpolated($@"
                        EXEC sp_GetStatsSummary 
                            @StartDate = {startDateTime}, 
                            @EndDate = {endDateTime}, 
                            @CameraId = {query.CameraId}")
                    .ToListAsync(ct);

                var summary = summaryList.FirstOrDefault();

                // 2. Get Daily Stats
                var dailyStats = await context.SpDailyStats
                    .FromSqlInterpolated($@"
                        EXEC sp_GetDailyStats 
                            @StartDate = {startDateTime}, 
                            @EndDate = {endDateTime}, 
                            @CameraId = {query.CameraId}")
                    .ToListAsync(ct);

                // 3. Get Camera Breakdown
                var cameraBreakdown = await context.SpCameraBreakdown
                    .FromSqlInterpolated($@"
                        EXEC sp_GetCameraBreakdown 
                            @StartDate = {startDateTime}, 
                            @EndDate = {endDateTime}")
                    .ToListAsync(ct);

                // Build Result
                var result = new HistoricalStatsDto
                {
                    StartDate = summary?.StartDate ?? startDateTime,
                    EndDate = summary?.EndDate ?? endDateTime,
                    TotalDays = summary?.TotalDays ?? 0,
                    TotalUniquePersons = summary?.TotalUniquePersons ?? 0,
                    TotalDetections = summary?.TotalDetections ?? 0,

                    DailyStats = dailyStats.Select(d => new DailyStatsDto
                    {
                        Date = d.Date,
                        DayName = d.DayName,
                        UniquePersons = d.UniquePersons,
                        TotalDetections = d.TotalDetections,
                        PeakHour = d.PeakHour,
                        PeakHourCount = d.PeakHourCount
                    }).ToList(),

                    CameraBreakdown = cameraBreakdown.Select(c => new CameraBreakdownDto
                    {
                        CameraId = c.CameraId,
                        CameraName = c.CameraName,
                        TotalDetections = c.TotalDetections,
                        UniquePersons = c.UniquePersons
                    }).ToList()
                };

                _logger.LogInformation(
                    "📊 Stats result: {Unique} unique, {Detections} detections, {Days} days",
                    result.TotalUniquePersons, result.TotalDetections, result.TotalDays);

                return result;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == -2) // Timeout
            {
                _logger.LogError(ex, "SQL timeout while fetching stats for range {Start} to {End}",
                    startDateTime, endDateTime);

                // Return empty result with error indication
                return new HistoricalStatsDto
                {
                    StartDate = startDateTime,
                    EndDate = endDateTime,
                    TotalDays = 0,
                    TotalUniquePersons = 0,
                    TotalDetections = 0,
                    DailyStats = new List<DailyStatsDto>(),
                    CameraBreakdown = new List<CameraBreakdownDto>(),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching historical stats");
                throw;
            }
        }
    }
}