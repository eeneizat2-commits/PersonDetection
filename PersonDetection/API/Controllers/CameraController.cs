namespace PersonDetection.API.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using OpenCvSharp;
    using PersonDetection.Application.Commands;
    using PersonDetection.Application.Common;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Application.Queries;
    using PersonDetection.Application.Services;

    [ApiController]
    [Route("api/[controller]")]
    public class CameraController : ControllerBase
    {
        private readonly ICommandDispatcher _commandDispatcher;
        private readonly IQueryDispatcher _queryDispatcher;
        private readonly ILogger<CameraController> _logger;
        private readonly IStreamProcessorFactory _streamProcessorFactory;

        public CameraController(
       ICommandDispatcher commandDispatcher,
       IQueryDispatcher queryDispatcher,
       IStreamProcessorFactory streamProcessorFactory,
       ILogger<CameraController> logger)
        {
            _commandDispatcher = commandDispatcher;
            _queryDispatcher = queryDispatcher;
            _streamProcessorFactory = streamProcessorFactory;
            _logger = logger;
        }


        [HttpPost("start")]
        public async Task<IActionResult> StartCamera([FromBody] StartCameraRequest request, CancellationToken ct)
        {
            try
            {
                var command = new StartCameraCommand(request.CameraId, request.Url);
                var result = await _commandDispatcher.Dispatch(command, ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start camera {CameraId}", request.CameraId);
                return StatusCode(500, new { error = "Failed to start camera" });
            }
        }

        [HttpPost("stop/{cameraId}")]
        public async Task<IActionResult> StopCamera(int cameraId, CancellationToken ct)
        {
            try
            {
                var command = new StopCameraCommand(cameraId);
                await _commandDispatcher.Dispatch(command, ct);
                return Ok(new { message = "Camera stopped", cameraId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop camera {CameraId}", cameraId);
                return StatusCode(500, new { error = "Failed to stop camera" });
            }
        }

        [HttpGet("{cameraId}/stats")]
        public async Task<IActionResult> GetStats(int cameraId, [FromQuery] int recentCount = 100, CancellationToken ct = default)
        {
            try
            {
                var query = new GetCameraStatsQuery(cameraId, recentCount);
                var result = await _queryDispatcher.Dispatch(query, ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get stats for camera {CameraId}", cameraId);
                return StatusCode(500, new { error = "Failed to retrieve stats" });
            }
        }

        [HttpGet("{cameraId}/stream")]
        public async Task GetStream(int cameraId, [FromQuery] string url, CancellationToken ct)
        {
            try
            {
                var processor = GetStreamProcessor(cameraId, url);
                if (processor == null)
                {
                    Response.StatusCode = 404;
                    return;
                }

                Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                Response.Headers.Append("Cache-Control", "no-cache");
                Response.Headers.Append("Connection", "close");

                await foreach (var frame in processor.GetAnnotatedFramesAsync(ct))
                {
                    var boundary = System.Text.Encoding.UTF8.GetBytes(
                        $"\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");

                    await Response.Body.WriteAsync(boundary, ct);
                    await Response.Body.WriteAsync(frame, ct);
                    await Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Stream closed for camera {CameraId}", cameraId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stream error for camera {CameraId}", cameraId);
            }
        }

        // Add to CameraController.cs

        [HttpGet("test-webcam/{index}")]
        public IActionResult TestWebcam(int index)
        {
            try
            {
                using var capture = new VideoCapture(index, VideoCaptureAPIs.DSHOW);

                if (!capture.IsOpened())
                {
                    return BadRequest(new
                    {
                        error = "Cannot open webcam",
                        index = index,
                        suggestion = "Try index 0, 1, or 2. Make sure webcam is not in use by another app."
                    });
                }

                using var frame = new Mat();
                capture.Read(frame);

                if (frame.Empty())
                {
                    return BadRequest(new { error = "Webcam opened but cannot read frame" });
                }

                var width = capture.Get(VideoCaptureProperties.FrameWidth);
                var height = capture.Get(VideoCaptureProperties.FrameHeight);
                var fps = capture.Get(VideoCaptureProperties.Fps);

                return Ok(new
                {
                    success = true,
                    cameraIndex = index,
                    resolution = $"{width}x{height}",
                    fps = fps,
                    backend = "DSHOW"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private IStreamProcessor? GetStreamProcessor(int cameraId, string url)
        {
            var processor = _streamProcessorFactory.Create(cameraId, url);
            return processor;
        }

    }

    public record StartCameraRequest(int CameraId, string Url);
    public record StopCameraCommand(int CameraId) : ICommand<Unit>;

    public class Unit { public static Unit Value { get; } = new(); }
}