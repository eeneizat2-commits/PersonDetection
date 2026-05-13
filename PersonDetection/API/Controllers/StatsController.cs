// PersonDetection.API/Controllers/StatsController.cs
using Microsoft.AspNetCore.Mvc;
using PersonDetection.Application.DTOs;
using Microsoft.EntityFrameworkCore;
using PersonDetection.Application.Queries;
using PersonDetection.Application.Services;
using PersonDetection.Infrastructure.Context;

namespace PersonDetection.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StatsController : ControllerBase
    {
        private readonly IQueryDispatcher _queryDispatcher;
        private readonly ILogger<StatsController> _logger;

        public StatsController(
            IQueryDispatcher queryDispatcher,
            ILogger<StatsController> logger)
        {
            _queryDispatcher = queryDispatcher;
            _logger = logger;
        }

        // PersonDetection.API/Controllers/StatsController.cs
        [HttpGet("historical")]
        public async Task<ActionResult<HistoricalStatsDto>> GetHistoricalStats(
            [FromQuery] int? lastDays = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            [FromQuery] string? startTime = null,
            [FromQuery] string? endTime = null,
            [FromQuery] string? cameraIds = null,  // ✅ Changed: comma-separated string
            CancellationToken ct = default)
        {
            try
            {
                // ✅ Parse comma-separated camera IDs
                List<int>? parsedCameraIds = null;
                if (!string.IsNullOrWhiteSpace(cameraIds))
                {
                    parsedCameraIds = cameraIds
                        .Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var id) ? id : 0)
                        .Where(id => id > 0)
                        .ToList();

                    if (parsedCameraIds.Count == 0)
                        parsedCameraIds = null;
                }

                var query = new GetHistoricalStatsQuery
                {
                    LastDays = lastDays,
                    StartDate = startDate,
                    EndDate = endDate,
                    CameraIds = parsedCameraIds  // ✅ Changed
                };

                if (!string.IsNullOrEmpty(startTime) && TimeSpan.TryParse(startTime, out var st))
                    query.StartTime = st;

                if (!string.IsNullOrEmpty(endTime) && TimeSpan.TryParse(endTime, out var et))
                    query.EndTime = et;

                var result = await _queryDispatcher.Dispatch<HistoricalStatsDto>(query, ct);
                return Ok(result);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Historical stats request cancelled by client");
                return Ok(new HistoricalStatsDto
                {
                    StartDate = startDate ?? DateTime.Today,
                    EndDate = endDate ?? DateTime.Today,
                    TotalDays = 0,
                    TotalUniquePersons = 0,
                    TotalDetections = 0,
                    DailyStats = new List<DailyStatsDto>(),
                    CameraBreakdown = new List<CameraBreakdownDto>()
                });
            }
        }

        [HttpGet("quick/{period}")]
        public async Task<ActionResult<HistoricalStatsDto>> GetQuickStats(
            [FromRoute] string period,
            [FromQuery] string? cameraIds = null,  // ✅ Changed
            CancellationToken ct = default)
        {
            try
            {
                // ✅ Parse comma-separated camera IDs
                List<int>? parsedCameraIds = null;
                if (!string.IsNullOrWhiteSpace(cameraIds))
                {
                    parsedCameraIds = cameraIds
                        .Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var id) ? id : 0)
                        .Where(id => id > 0)
                        .ToList();

                    if (parsedCameraIds.Count == 0)
                        parsedCameraIds = null;
                }

                var today = DateTime.Today;

                int days = period.ToLower() switch
                {
                    "today" => 1,
                    "yesterday" => 1,
                    "week" => 7,
                    "month" => 30,
                    "3days" => 3,
                    "4days" => 4,
                    _ => 7
                };

                var query = new GetHistoricalStatsQuery
                {
                    LastDays = days,
                    CameraIds = parsedCameraIds  // ✅ Changed
                };

                if (period.ToLower() == "yesterday")
                {
                    query.LastDays = null;
                    var yesterday = today.AddDays(-1);
                    query.StartDate = yesterday;
                    query.EndDate = yesterday;
                }

                var result = await _queryDispatcher.Dispatch<HistoricalStatsDto>(query, ct);
                return Ok(result);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Quick stats request cancelled by client");
                return Ok(new HistoricalStatsDto
                {
                    DailyStats = new List<DailyStatsDto>(),
                    CameraBreakdown = new List<CameraBreakdownDto>()
                });
            }
        }

        [HttpGet("summary")]
        public async Task<ActionResult<SummaryStatsDto>> GetSummary(
            [FromQuery] string? cameraIds = null,  // ✅ NEW: Optional camera filter
            CancellationToken ct = default)
        {
            try
            {
                // ✅ Parse comma-separated camera IDs
                List<int>? parsedCameraIds = null;
                if (!string.IsNullOrWhiteSpace(cameraIds))
                {
                    parsedCameraIds = cameraIds
                        .Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var id) ? id : 0)
                        .Where(id => id > 0)
                        .ToList();

                    if (parsedCameraIds.Count == 0)
                        parsedCameraIds = null;
                }

                var todayQuery = new GetHistoricalStatsQuery
                {
                    LastDays = 1,
                    CameraIds = parsedCameraIds  // ✅ Changed
                };
                var weekQuery = new GetHistoricalStatsQuery
                {
                    LastDays = 7,
                    CameraIds = parsedCameraIds  // ✅ Changed
                };
                var monthQuery = new GetHistoricalStatsQuery
                {
                    LastDays = 30,
                    CameraIds = parsedCameraIds  // ✅ Changed
                };

                var today = await _queryDispatcher.Dispatch<HistoricalStatsDto>(todayQuery, ct);

                ct.ThrowIfCancellationRequested();

                var week = await _queryDispatcher.Dispatch<HistoricalStatsDto>(weekQuery, ct);

                ct.ThrowIfCancellationRequested();

                var month = await _queryDispatcher.Dispatch<HistoricalStatsDto>(monthQuery, ct);

                return Ok(new SummaryStatsDto
                {
                    Today = new PeriodStatsDto
                    {
                        UniquePersons = today.TotalUniquePersons,
                        Detections = today.TotalDetections,
                        DailyAverage = today.TotalUniquePersons
                    },
                    ThisWeek = new PeriodStatsDto
                    {
                        UniquePersons = week.TotalUniquePersons,
                        Detections = week.TotalDetections,
                        DailyAverage = week.TotalDays > 0
                            ? week.TotalUniquePersons / week.TotalDays : 0
                    },
                    ThisMonth = new PeriodStatsDto
                    {
                        UniquePersons = month.TotalUniquePersons,
                        Detections = month.TotalDetections,
                        DailyAverage = month.TotalDays > 0
                            ? month.TotalUniquePersons / month.TotalDays : 0
                    }
                });
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Summary stats request cancelled by client");
                var empty = new PeriodStatsDto();
                return Ok(new SummaryStatsDto
                {
                    Today = empty,
                    ThisWeek = empty,
                    ThisMonth = empty
                });
            }
        }
    }
}