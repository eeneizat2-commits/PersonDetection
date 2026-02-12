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

        // ★ Using Handle (not HandleAsync) to match your pattern
        public async Task<HistoricalStatsDto> Handle(GetHistoricalStatsQuery query, CancellationToken ct = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            // Determine date range
            DateTime endDate = query.EndDate?.Date.AddDays(1) ?? DateTime.UtcNow.Date.AddDays(1);
            DateTime startDate;

            if (query.LastDays.HasValue && query.LastDays.Value > 0)
            {
                startDate = DateTime.UtcNow.Date.AddDays(-query.LastDays.Value + 1);
            }
            else if (query.StartDate.HasValue)
            {
                startDate = query.StartDate.Value.Date;
            }
            else
            {
                startDate = DateTime.UtcNow.Date.AddDays(-6); // Default: last 7 days
            }

            _logger.LogInformation("📊 Fetching stats from {Start} to {End}", startDate, endDate);

            var result = new HistoricalStatsDto
            {
                StartDate = startDate,
                EndDate = endDate.AddDays(-1),
                TotalDays = (int)(endDate - startDate).TotalDays
            };

            // Get unique persons in date range
            var uniquePersonsQuery = context.UniquePersons
                .Where(p => p.IsActive &&
                           ((p.FirstSeenAt >= startDate && p.FirstSeenAt < endDate) ||
                            (p.LastSeenAt >= startDate && p.LastSeenAt < endDate)));

            if (query.CameraId.HasValue)
            {
                uniquePersonsQuery = uniquePersonsQuery
                    .Where(p => p.FirstSeenCameraId == query.CameraId || p.LastSeenCameraId == query.CameraId);
            }

            result.TotalUniquePersons = await uniquePersonsQuery.CountAsync(ct);

            // Get total detections
            var detectionsQuery = context.DetectionResults
                .Where(d => d.Timestamp >= startDate && d.Timestamp < endDate);

            if (query.CameraId.HasValue)
            {
                detectionsQuery = detectionsQuery.Where(d => d.CameraId == query.CameraId);
            }

            result.TotalDetections = await detectionsQuery.SumAsync(d => d.TotalDetections, ct);

            // Get daily breakdown
            result.DailyStats = await GetDailyStatsAsync(context, startDate, endDate, query.CameraId, ct);

            // Get camera breakdown
            result.CameraBreakdown = await GetCameraBreakdownAsync(context, startDate, endDate, ct);

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

            for (var date = startDate; date < endDate; date = date.AddDays(1))
            {
                var nextDate = date.AddDays(1);

                // Unique persons first seen on this day
                var uniqueQuery = context.UniquePersons
                    .Where(p => p.IsActive && p.FirstSeenAt >= date && p.FirstSeenAt < nextDate);

                if (cameraId.HasValue)
                {
                    uniqueQuery = uniqueQuery.Where(p => p.FirstSeenCameraId == cameraId);
                }

                var uniqueCount = await uniqueQuery.CountAsync(ct);

                // Total detections for this day
                var detectionsQuery = context.DetectionResults
                    .Where(d => d.Timestamp >= date && d.Timestamp < nextDate);

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

            return dailyStats;
        }

        private async Task<List<CameraBreakdownDto>> GetCameraBreakdownAsync(
            DetectionContext context,
            DateTime startDate,
            DateTime endDate,
            CancellationToken ct)
        {
            // Get camera stats
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

            // Get camera names
            var cameraIds = cameraStats.Select(c => c.CameraId).ToList();

            // ★ FIX: Use Cameras (not CameraConfigs)
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