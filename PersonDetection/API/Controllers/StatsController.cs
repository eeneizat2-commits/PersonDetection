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

        [HttpGet("historical")]
        public async Task<ActionResult<HistoricalStatsDto>> GetHistoricalStats(
            [FromQuery] int? lastDays = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            [FromQuery] string? startTime = null,
            [FromQuery] string? endTime = null,
            [FromQuery] int? cameraId = null,
            CancellationToken ct = default)
        {
            try
            {
                var query = new GetHistoricalStatsQuery
                {
                    LastDays = lastDays,
                    StartDate = startDate,
                    EndDate = endDate,
                    CameraId = cameraId
                };

                if (!string.IsNullOrEmpty(startTime) && TimeSpan.TryParse(startTime, out var st))
                    query.StartTime = st;

                if (!string.IsNullOrEmpty(endTime) && TimeSpan.TryParse(endTime, out var et))
                    query.EndTime = et;

                var result = await _queryDispatcher.Dispatch<HistoricalStatsDto>(query, ct);
                return Ok(result);
            }
            // ✅ FIX: Catch cancellation at the controller level too
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Historical stats request cancelled by client");
                // Return empty 200 so Angular doesn't get a network error
                // Alternatively: return StatusCode(499);
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
            [FromQuery] int? cameraId = null,
            CancellationToken ct = default)
        {
            try
            {
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
                    CameraId = cameraId
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
            CancellationToken ct = default)
        {
            try
            {
                var todayQuery = new GetHistoricalStatsQuery { LastDays = 1 };
                var weekQuery = new GetHistoricalStatsQuery { LastDays = 7 };
                var monthQuery = new GetHistoricalStatsQuery { LastDays = 30 };

                var today = await _queryDispatcher.Dispatch<HistoricalStatsDto>(todayQuery, ct);

                // ✅ FIX: Check between sequential queries
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