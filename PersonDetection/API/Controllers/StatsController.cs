// PersonDetection.API/Controllers/StatsController.cs
using Microsoft.AspNetCore.Mvc;
using PersonDetection.Application.DTOs;
using PersonDetection.Application.Queries;
using PersonDetection.Application.Services;

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

        /// <summary>
        /// Get historical statistics for a date range
        /// </summary>
        [HttpGet("historical")]
        public async Task<ActionResult<HistoricalStatsDto>> GetHistoricalStats(
            [FromQuery] int? lastDays = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            [FromQuery] int? cameraId = null,
            CancellationToken ct = default)
        {
            var query = new GetHistoricalStatsQuery
            {
                LastDays = lastDays,
                StartDate = startDate,
                EndDate = endDate,
                CameraId = cameraId
            };

            // ★ Using Dispatch (not DispatchAsync)
            var result = await _queryDispatcher.Dispatch<HistoricalStatsDto>(query, ct);
            return Ok(result);
        }

        /// <summary>
        /// Get quick stats for predefined periods
        /// </summary>
        [HttpGet("quick/{period}")]
        public async Task<ActionResult<HistoricalStatsDto>> GetQuickStats(
            [FromRoute] string period,
            [FromQuery] int? cameraId = null,
            CancellationToken ct = default)
        {
            int days = period.ToLower() switch
            {
                "today" => 1,
                "yesterday" => 2,
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

            // Special handling for "yesterday"
            if (period.ToLower() == "yesterday")
            {
                query.LastDays = null;
                query.StartDate = DateTime.UtcNow.Date.AddDays(-1);
                query.EndDate = DateTime.UtcNow.Date;
            }

            var result = await _queryDispatcher.Dispatch<HistoricalStatsDto>(query, ct);
            return Ok(result);
        }

        /// <summary>
        /// Get summary counts for dashboard widgets
        /// </summary>
        [HttpGet("summary")]
        public async Task<ActionResult<SummaryStatsDto>> GetSummary(CancellationToken ct = default)
        {
            var todayQuery = new GetHistoricalStatsQuery { LastDays = 1 };
            var weekQuery = new GetHistoricalStatsQuery { LastDays = 7 };
            var monthQuery = new GetHistoricalStatsQuery { LastDays = 30 };

            var today = await _queryDispatcher.Dispatch<HistoricalStatsDto>(todayQuery, ct);
            var week = await _queryDispatcher.Dispatch<HistoricalStatsDto>(weekQuery, ct);
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
                    DailyAverage = week.TotalDays > 0 ? week.TotalUniquePersons / week.TotalDays : 0
                },
                ThisMonth = new PeriodStatsDto
                {
                    UniquePersons = month.TotalUniquePersons,
                    Detections = month.TotalDetections,
                    DailyAverage = month.TotalDays > 0 ? month.TotalUniquePersons / month.TotalDays : 0
                }
            });
        }
    }
}