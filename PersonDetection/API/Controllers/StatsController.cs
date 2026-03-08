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

        /// <summary>
        /// Get historical statistics for a date range
        /// </summary>
        [HttpGet("historical")]
        public async Task<ActionResult<HistoricalStatsDto>> GetHistoricalStats(
          [FromQuery] int? lastDays = null,
          [FromQuery] DateTime? startDate = null,
          [FromQuery] DateTime? endDate = null,
          [FromQuery] string? startTime = null,  // NEW: "HH:mm" format
          [FromQuery] string? endTime = null,    // NEW: "HH:mm" format
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

            // Parse time if provided
            if (!string.IsNullOrEmpty(startTime) && TimeSpan.TryParse(startTime, out var st))
            {
                query.StartTime = st;
            }

            if (!string.IsNullOrEmpty(endTime) && TimeSpan.TryParse(endTime, out var et))
            {
                query.EndTime = et;
            }

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
            var today = DateTime.Today; // Use local date

            int days = period.ToLower() switch
            {
                "today" => 1,
                "yesterday" => 1,  // Changed - we'll handle specially
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

            // Special handling for "yesterday" - use custom date range
            if (period.ToLower() == "yesterday")
            {
                query.LastDays = null;
                var yesterday = today.AddDays(-1);
                query.StartDate = yesterday;
                query.EndDate = yesterday; // Same day (single day range)
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


        /// <summary>
        /// DEBUG: Test SP directly via raw ADO.NET (bypass EF Core)
        /// </summary>
        [HttpGet("debug-sp")]
        public async Task<ActionResult> DebugSp(CancellationToken ct)
        {
            using var scope = HttpContext.RequestServices.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

            var startDate = DateTime.Today.AddDays(-2);  // 3 days ago
            var endDate = DateTime.Today.AddDays(1).AddTicks(-1);  // End of today

            _logger.LogInformation("DEBUG SP: {Start} to {End}", startDate, endDate);

            // ═══════════════════════════════════════════
            // TEST 1: EF Core FromSqlInterpolated
            // ═══════════════════════════════════════════
            object? efResult = null;
            string? efError = null;
            try
            {
                var summaryList = await context.SpStatsSummary
                    .FromSqlInterpolated($@"
                EXEC sp_GetStatsSummary 
                    @StartDate = {startDate}, 
                    @EndDate = {endDate}, 
                    @CameraId = {(int?)null}")
                    .ToListAsync(ct);

                efResult = new
                {
                    count = summaryList.Count,
                    data = summaryList.FirstOrDefault()
                };
            }
            catch (Exception ex)
            {
                efError = ex.ToString();
            }

            // ═══════════════════════════════════════════
            // TEST 2: Raw ADO.NET (bypass EF Core)
            // ═══════════════════════════════════════════
            object? rawResult = null;
            string? rawError = null;
            try
            {
                var connection = context.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(ct);

                using var command = connection.CreateCommand();
                command.CommandText = "sp_GetStatsSummary";
                command.CommandType = System.Data.CommandType.StoredProcedure;

                var p1 = command.CreateParameter();
                p1.ParameterName = "@StartDate";
                p1.Value = startDate;
                command.Parameters.Add(p1);

                var p2 = command.CreateParameter();
                p2.ParameterName = "@EndDate";
                p2.Value = endDate;
                command.Parameters.Add(p2);

                var p3 = command.CreateParameter();
                p3.ParameterName = "@CameraId";
                p3.Value = DBNull.Value;
                command.Parameters.Add(p3);

                using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    rawResult = new
                    {
                        startDate = reader.GetDateTime(0),
                        endDate = reader.GetDateTime(1),
                        totalDays = reader.GetInt32(2),
                        totalUniquePersons = reader.GetInt32(3),
                        totalDetections = reader.GetInt32(4)
                    };
                }
                else
                {
                    rawResult = "No rows returned";
                }
            }
            catch (Exception ex)
            {
                rawError = ex.ToString();
            }

            return Ok(new
            {
                parameters = new { startDate, endDate },
                efCore = new { result = efResult, error = efError },
                rawAdoNet = new { result = rawResult, error = rawError }
            });
        }
    }
}