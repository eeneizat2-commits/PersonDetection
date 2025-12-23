namespace PersonDetection.API.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using PersonDetection.Application.Queries;
    using PersonDetection.Application.Services;

    [ApiController]
    [Route("api/[controller]")]
    public class DetectionController : ControllerBase
    {
        private readonly IQueryDispatcher _queryDispatcher;

        public DetectionController(IQueryDispatcher queryDispatcher)
        {
            _queryDispatcher = queryDispatcher;
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
    }
}
