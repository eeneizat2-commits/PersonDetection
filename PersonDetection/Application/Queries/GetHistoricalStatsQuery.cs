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
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? LastDays { get; set; }
        public int? CameraId { get; set; }
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
            // FIX: Use local date (DateTime.Today) instead of DateTime.UtcNow
            // ═══════════════════════════════════════════════════════════════
            var today = DateTime.Today; // Local date, midnight

            DateTime startDate;
            DateTime endDate;

            if (query.StartDate.HasValue && query.EndDate.HasValue)
            {
                // Custom range - use the dates as provided (local dates)
                startDate = query.StartDate.Value.Date;
                endDate = query.EndDate.Value.Date;
            }
            else if (query.LastDays.HasValue && query.LastDays.Value > 0)
            {
                // Last N days - include today
                endDate = today;
                startDate = today.AddDays(-(query.LastDays.Value - 1));
            }
            else
            {
                // Default: last 7 days including today
                endDate = today;
                startDate = today.AddDays(-6);
            }

            // For querying: startDate is inclusive, endDate needs +1 day for exclusive upper bound
            var queryStartDate = startDate.Date;
            var queryEndDate = endDate.Date.AddDays(1); // Exclusive end

            _logger.LogInformation(
                "📊 Fetching stats: Display [{Start:yyyy-MM-dd}] to [{End:yyyy-MM-dd}], Query [{QStart:yyyy-MM-dd}] to [{QEnd:yyyy-MM-dd})",
                startDate, endDate, queryStartDate, queryEndDate);

            var result = new HistoricalStatsDto
            {
                StartDate = startDate,  // Display start date
                EndDate = endDate,      // Display end date (inclusive)
                TotalDays = (endDate - startDate).Days + 1  // +1 because both dates are inclusive
            };

            // Get unique persons in date range
            var uniquePersonsQuery = context.UniquePersons
                .Where(p => p.IsActive &&
                           ((p.FirstSeenAt >= queryStartDate && p.FirstSeenAt < queryEndDate) ||
                            (p.LastSeenAt >= queryStartDate && p.LastSeenAt < queryEndDate)));

            if (query.CameraId.HasValue)
            {
                uniquePersonsQuery = uniquePersonsQuery
                    .Where(p => p.FirstSeenCameraId == query.CameraId || p.LastSeenCameraId == query.CameraId);
            }

            result.TotalUniquePersons = await uniquePersonsQuery.CountAsync(ct);

            // Get total detections
            var detectionsQuery = context.DetectionResults
                .Where(d => d.Timestamp >= queryStartDate && d.Timestamp < queryEndDate);

            if (query.CameraId.HasValue)
            {
                detectionsQuery = detectionsQuery.Where(d => d.CameraId == query.CameraId);
            }

            result.TotalDetections = await detectionsQuery.SumAsync(d => d.TotalDetections, ct);

            // Get daily breakdown
            result.DailyStats = await GetDailyStatsAsync(context, startDate, endDate, query.CameraId, ct);

            // Get camera breakdown
            result.CameraBreakdown = await GetCameraBreakdownAsync(context, queryStartDate, queryEndDate, ct);

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