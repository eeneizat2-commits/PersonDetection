// PersonDetection.API/Controllers/CameraConfigController.cs
namespace PersonDetection.API.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using PersonDetection.Application.Commands;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Repositories;

    [ApiController]
    [Route("api/cameras")]
    public class CameraConfigController : ControllerBase
    {
        private readonly ICameraConfigRepository _cameraRepo;
        private readonly IStreamProcessorFactory _processorFactory;
        private readonly ILogger<CameraConfigController> _logger;

        public CameraConfigController(
            ICameraConfigRepository cameraRepo,
            IStreamProcessorFactory processorFactory,
            ILogger<CameraConfigController> logger)
        {
            _cameraRepo = cameraRepo;
            _processorFactory = processorFactory;
            _logger = logger;
        }

        /// <summary>
        /// Get all cameras
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<CameraDto>>> GetAll(CancellationToken ct)
        {
            var cameras = await _cameraRepo.GetAllAsync(ct);
            var processors = _processorFactory.GetAll();  // Returns IReadOnlyDictionary<int, IStreamProcessor>

            var dtos = cameras.Select(c => new CameraDto(
                c.Id,
                c.Name,
                c.Url,
                c.Description,
                c.Type,
                c.IsEnabled,
                c.CreatedAt,
                c.LastConnectedAt,
                c.DisplayOrder,
                processors.TryGetValue(c.Id, out var processor) && processor.IsConnected  // ✅ Fixed
            )).ToList();

            return Ok(dtos);
        }

        /// <summary>
        /// Get single camera
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<CameraDto>> GetById(int id, CancellationToken ct)
        {
            var camera = await _cameraRepo.GetByIdAsync(id, ct);
            if (camera == null)
                return NotFound(new { error = "Camera not found" });

            var processor = _processorFactory.Get(id);
            var isActive = processor?.IsConnected ?? false;

            return Ok(new CameraDto(
                camera.Id,
                camera.Name,
                camera.Url,
                camera.Description,
                camera.Type,
                camera.IsEnabled,
                camera.CreatedAt,
                camera.LastConnectedAt,
                camera.DisplayOrder,
                isActive
            ));
        }

        /// <summary>
        /// Create new camera
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<CameraDto>> Create([FromBody] CreateCameraRequest request, CancellationToken ct)
        {
            try
            {
                var camera = Camera.Create(
                    request.Name,
                    request.Url,
                    request.Type,
                    request.Description
                );

                var id = await _cameraRepo.CreateAsync(camera, ct);

                _logger.LogInformation("Camera created: {Id} - {Name}", id, request.Name);

                return CreatedAtAction(nameof(GetById), new { id }, new CameraDto(
                    camera.Id,
                    camera.Name,
                    camera.Url,
                    camera.Description,
                    camera.Type,
                    camera.IsEnabled,
                    camera.CreatedAt,
                    camera.LastConnectedAt,
                    camera.DisplayOrder,
                    false
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create camera");
                return StatusCode(500, new { error = "Failed to create camera" });
            }
        }

        /// <summary>
        /// Update camera
        /// </summary>
        [HttpPut("{id}")]
        public async Task<ActionResult<CameraDto>> Update(int id, [FromBody] UpdateCameraRequest request, CancellationToken ct)
        {
            try
            {
                var camera = await _cameraRepo.GetByIdAsync(id, ct);
                if (camera == null)
                    return NotFound(new { error = "Camera not found" });

                camera.Name = request.Name;
                camera.Url = request.Url;
                camera.Description = request.Description;
                camera.Type = request.Type;
                camera.IsEnabled = request.IsEnabled;
                camera.DisplayOrder = request.DisplayOrder;

                await _cameraRepo.UpdateAsync(camera, ct);

                // Stop processor if camera is disabled
                if (!request.IsEnabled)
                {
                    _processorFactory.Remove(id);
                }

                _logger.LogInformation("Camera updated: {Id} - {Name}", id, request.Name);

                return Ok(new CameraDto(
                    camera.Id,
                    camera.Name,
                    camera.Url,
                    camera.Description,
                    camera.Type,
                    camera.IsEnabled,
                    camera.CreatedAt,
                    camera.LastConnectedAt,
                    camera.DisplayOrder,
                    _processorFactory.Get(id)?.IsConnected ?? false
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update camera {Id}", id);
                return StatusCode(500, new { error = "Failed to update camera" });
            }
        }

        /// <summary>
        /// Delete camera
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<ActionResult> Delete(int id, CancellationToken ct)
        {
            try
            {
                // Stop processor first
                _processorFactory.Remove(id);

                await _cameraRepo.DeleteAsync(id, ct);

                _logger.LogInformation("Camera deleted: {Id}", id);

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete camera {Id}", id);
                return StatusCode(500, new { error = "Failed to delete camera" });
            }
        }
    }
}