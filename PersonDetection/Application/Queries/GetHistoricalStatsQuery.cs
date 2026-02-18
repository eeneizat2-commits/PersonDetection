// PersonDetection.Application/Queries/GetHistoricalStatsQuery.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PersonDetection.Application.Common;
using PersonDetection.Application.DTOs;
using PersonDetection.Infrastructure.Context;

namespace PersonDetection.Application.Queries
{
    // Query must implement IQuery<TResult>
    public class GetHistoricalStatsQuery : IQuery<HistoricalStatsDto>
    {
        public int? LastDays { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? CameraId { get; set; }

        // NEW: Optional time components
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
    }

    // Handler must implement IQueryHandler with Handle method
    public class GetHistoricalStatsHandler : IQueryHandler<GetHistoricalStatsQuery, HistoricalStatsDto>
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GetHistoricalStatsHandler> _logger;

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
            // Calculate date range with optional time
            // ═══════════════════════════════════════════════════════════════
            var today = DateTime.Today;
            DateTime startDateTime;
            DateTime endDateTime;

            if (query.StartDate.HasValue && query.EndDate.HasValue)
            {
                // Custom range with optional time
                startDateTime = query.StartDate.Value.Date;
                endDateTime = query.EndDate.Value.Date;

                // Add time if provided
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
                    // Default: end of day (23:59:59)
                    endDateTime = endDateTime.AddDays(1).AddTicks(-1);
                }
            }
            else if (query.LastDays.HasValue && query.LastDays.Value > 0)
            {
                // Last N days including today (full days)
                endDateTime = today.AddDays(1).AddTicks(-1); // End of today
                startDateTime = today.AddDays(-(query.LastDays.Value - 1)); // Start of first day
            }
            else
            {
                // Default: last 7 days
                endDateTime = today.AddDays(1).AddTicks(-1);
                startDateTime = today.AddDays(-6);
            }

            _logger.LogInformation(
                "📊 SP Stats: [{Start:yyyy-MM-dd HH:mm}] to [{End:yyyy-MM-dd HH:mm}], Camera: {Camera}",
                startDateTime, endDateTime, query.CameraId?.ToString() ?? "All");

            // ═══════════════════════════════════════════════════════════════
            // Call Stored Procedures
            // ═══════════════════════════════════════════════════════════════

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

            // ═══════════════════════════════════════════════════════════════
            // Build Result
            // ═══════════════════════════════════════════════════════════════
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

        private async Task<List<DailyStatsDto>> GetDailyStatsAsync(
            DetectionContext context,
            DateTime startDate,
            DateTime endDate,
            int? cameraId,
            CancellationToken ct)
        {
            var dailyStats = new List<DailyStatsDto>();

            // Iterate through each day INCLUSIVE of both start and end dates
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                var dayStart = date;
                var dayEnd = date.AddDays(1); // Exclusive end for this day

                // Unique persons first seen on this day
                var uniqueQuery = context.UniquePersons
                    .Where(p => p.IsActive && p.FirstSeenAt >= dayStart && p.FirstSeenAt < dayEnd);

                if (cameraId.HasValue)
                {
                    uniqueQuery = uniqueQuery.Where(p => p.FirstSeenCameraId == cameraId);
                }

                var uniqueCount = await uniqueQuery.CountAsync(ct);

                // Total detections for this day
                var detectionsQuery = context.DetectionResults
                    .Where(d => d.Timestamp >= dayStart && d.Timestamp < dayEnd);

                if (cameraId.HasValue)
                {
                    detectionsQuery = detectionsQuery.Where(d => d.CameraId == cameraId);
                }

                var totalDetections = await detectionsQuery.SumAsync(d => d.TotalDetections, ct);

                // Find peak hour
                var hourlyData = await detectionsQuery
                    .GroupBy(d => d.Timestamp.Hour)
                    .Select(g => new { Hour = g.Key, Count = g.Sum(x => x.TotalDetections) })
                    .OrderByDescending(x => x.Count)
                    .FirstOrDefaultAsync(ct);

                dailyStats.Add(new DailyStatsDto
                {
                    Date = date,
                    DayName = date.ToString("dddd"),
                    UniquePersons = uniqueCount,
                    TotalDetections = totalDetections,
                    PeakHour = hourlyData?.Hour ?? 0,
                    PeakHourCount = hourlyData?.Count ?? 0
                });
            }

            return dailyStats.OrderBy(d => d.Date).ToList();
        }

        private async Task<List<CameraBreakdownDto>> GetCameraBreakdownAsync(
            DetectionContext context,
            DateTime startDate,
            DateTime endDate,
            CancellationToken ct)
        {
            var cameraStats = await context.DetectionResults
                .Where(d => d.Timestamp >= startDate && d.Timestamp < endDate)
                .GroupBy(d => d.CameraId)
                .Select(g => new CameraBreakdownDto
                {
                    CameraId = g.Key,
                    TotalDetections = g.Sum(x => x.TotalDetections),
                    UniquePersons = g.Sum(x => x.UniquePersonCount)
                })
                .ToListAsync(ct);

            var cameraIds = cameraStats.Select(c => c.CameraId).ToList();

            var cameras = await context.Cameras
                .Where(c => cameraIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name })
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

            foreach (var stat in cameraStats)
            {
                stat.CameraName = cameras.TryGetValue(stat.CameraId, out var name)
                    ? name
                    : $"Camera {stat.CameraId}";
            }

            return cameraStats.OrderByDescending(c => c.TotalDetections).ToList();
        }
    }
}