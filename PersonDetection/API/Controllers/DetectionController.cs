namespace PersonDetection.API.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using PersonDetection.Application.Queries;
    using PersonDetection.Application.Services;
    using PersonDetection.Domain.Services;

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
    }
}