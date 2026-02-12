namespace PersonDetection.API.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using PersonDetection.Application.Queries;
    using PersonDetection.Application.Services;
    using PersonDetection.Domain.Services;
    using PersonDetection.Infrastructure.Identity;

    [ApiController]
    [Route("api/[controller]")]
    public class DetectionController : ControllerBase
    {
        private readonly IQueryDispatcher _queryDispatcher;
        private readonly IPersonIdentityMatcher _identityMatcher;

        public DetectionController(
            IQueryDispatcher queryDispatcher,
            IPersonIdentityMatcher identityMatcher)  // Add this
        {
            _queryDispatcher = queryDispatcher;
            _identityMatcher = identityMatcher;
        }

        [HttpGet("active")]
        public async Task<IActionResult> GetActiveCameras(CancellationToken ct)
        {
            var query = new GetActiveCamerasQuery();
            var result = await _queryDispatcher.Dispatch(query, ct);
            return Ok(result);
        }

        [HttpGet("camera/{cameraId}")]
        public async Task<IActionResult> GetCameraDetections(int cameraId, CancellationToken ct)
        {
            var query = new GetCameraStatsQuery(cameraId);
            var result = await _queryDispatcher.Dispatch(query, ct);
            return Ok(result);
        }

        [HttpPost("reset-identities")]
        public IActionResult ResetIdentities()
        {
            _identityMatcher.ClearAllIdentities();  // Fixed: use _identityMatcher
            return Ok(new { message = "All identities cleared", timestamp = DateTime.UtcNow });
        }

        [HttpGet("identity-count")]
        public IActionResult GetIdentityCount()
        {
            var count = _identityMatcher.GetActiveIdentityCount();
            return Ok(new { activeIdentities = count });
        }

        [HttpGet("stats")]
        public IActionResult GetGlobalStats()
        {
            var todayUnique = _identityMatcher.GetTodayUniqueCount();
            var totalInMemory = _identityMatcher.GetActiveIdentityCount();
            var confirmed = _identityMatcher.GetConfirmedIdentityCount();

            return Ok(new
            {
                todayUniqueCount = todayUnique,
                totalInMemory = totalInMemory,
                confirmedCount = confirmed,
                timestamp = DateTime.UtcNow
            });
        }

        [HttpPost("reload-from-database")]
        public async Task<IActionResult> ReloadFromDatabase()
        {
            if (_identityMatcher is PersonIdentityService service)
            {
                await service.ReloadFromDatabaseAsync();
                return Ok(new { message = "Reloaded from database", count = _identityMatcher.GetActiveIdentityCount() });
            }
            return BadRequest("Service doesn't support reload");
        }

        // In DetectionController.cs
        [HttpPost("new-session")]
        public IActionResult StartNewSession()
        {
            if (_identityMatcher is PersonIdentityService service)
            {
                service.StartNewSession();
                return Ok(new
                {
                    message = "New session started",
                    timestamp = DateTime.UtcNow
                });
            }
            return BadRequest("Service doesn't support sessions");
        }

        [HttpGet("counts")]
        public IActionResult GetCounts()
        {
            return Ok(new
            {
                globalUnique = _identityMatcher.GetConfirmedIdentityCount(),
                totalInMemory = _identityMatcher.GetActiveIdentityCount(),
                timestamp = DateTime.UtcNow
            });
        }
    }
}