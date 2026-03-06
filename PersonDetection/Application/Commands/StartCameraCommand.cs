namespace PersonDetection.Application.Commands
{
    using PersonDetection.Application.Common;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Repositories;
    using PersonDetection.Infrastructure.Services;

    public record StartCameraCommand(int CameraId, string Url) : ICommand<CameraSessionDto>;

    public record CameraSessionDto(int CameraId, string Url, bool IsActive, DateTime StartedAt);

    public class StartCameraHandler : ICommandHandler<StartCameraCommand, CameraSessionDto>
    {
        private readonly IUnitOfWork _uow;
        private readonly IStreamProcessorFactory _processorFactory;
        private readonly CameraHealthCheckService? _healthCheckService;  // ✅ ADD
        private readonly ILogger<StartCameraHandler> _logger;

        public StartCameraHandler(
            IUnitOfWork uow,
            IStreamProcessorFactory processorFactory,
            CameraHealthCheckService? healthCheckService,              // ✅ ADD
            ILogger<StartCameraHandler> logger)
        {
            _uow = uow;
            _processorFactory = processorFactory;
            _logger = logger;
        }

        public async Task<CameraSessionDto> Handle(StartCameraCommand cmd, CancellationToken ct)
        {
            _logger.LogInformation("Starting camera {CameraId} with URL: {Url}", cmd.CameraId, cmd.Url);

            // ✅ Clear manual stop flag
            _healthCheckService?.ClearManuallyStopped(cmd.CameraId);

            // Stop any existing active session
            var existingSession = await _uow.Cameras.GetActiveSessionAsync(cmd.CameraId, ct);
            if (existingSession != null)
            {
                existingSession.Stop();
                await _uow.Cameras.UpdateSessionAsync(existingSession, ct);
            }

            // Create new session
            var session = CameraSession.Start(cmd.CameraId, cmd.Url);
            await _uow.Cameras.CreateSessionAsync(session, ct);
            await _uow.SaveChangesAsync(ct);

            // Start stream processor
            var processor = _processorFactory.Create(cmd.CameraId, cmd.Url);
            await processor.ConnectAsync(cmd.Url, ct);

            return new CameraSessionDto(session.CameraId, session.Url, session.IsActive, session.StartedAt);
        }
    }

    public interface IStreamProcessorFactory
    {
        IStreamProcessor Create(int cameraId, string url);
        IStreamProcessor? Get(int cameraId);
        IReadOnlyDictionary<int, IStreamProcessor> GetAll();
        void Remove(int cameraId);
    }
}