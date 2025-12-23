// PersonDetection.Application/Commands/StopCameraHandler.cs
namespace PersonDetection.Application.Commands
{
    using PersonDetection.API.Controllers;
    using PersonDetection.Application.Common;
    using PersonDetection.Domain.Repositories;

    public class StopCameraHandler : ICommandHandler<StopCameraCommand, Unit>
    {
        private readonly IUnitOfWork _uow;
        private readonly IStreamProcessorFactory _processorFactory;
        private readonly ILogger<StopCameraHandler> _logger;

        public StopCameraHandler(
            IUnitOfWork uow,
            IStreamProcessorFactory processorFactory,
            ILogger<StopCameraHandler> logger)
        {
            _uow = uow;
            _processorFactory = processorFactory;
            _logger = logger;
        }

        public async Task<Unit> Handle(StopCameraCommand cmd, CancellationToken ct)
        {
            _logger.LogInformation("Stopping camera {CameraId}", cmd.CameraId);

            // Stop and remove stream processor
            _processorFactory.Remove(cmd.CameraId);

            // Update session in database
            var session = await _uow.Cameras.GetActiveSessionAsync(cmd.CameraId, ct);
            if (session != null)
            {
                session.Stop();
                await _uow.Cameras.UpdateSessionAsync(session, ct);
                await _uow.SaveChangesAsync(ct);
            }

            return Unit.Value;
        }
    }
}