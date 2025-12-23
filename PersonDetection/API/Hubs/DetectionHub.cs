namespace PersonDetection.API.Hubs
{
    using Microsoft.AspNetCore.SignalR;
    using PersonDetection.Domain.Services;

    public class DetectionHub : Hub
    {
        private readonly IPersonIdentityMatcher _identityMatcher;
        private readonly ILogger<DetectionHub> _logger;

        public DetectionHub(IPersonIdentityMatcher identityMatcher, ILogger<DetectionHub> logger)
        {
            _identityMatcher = identityMatcher;
            _logger = logger;
        }

        public async Task SubscribeToCamera(int cameraId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"camera_{cameraId}");
            _logger.LogInformation("Client {ConnectionId} subscribed to camera {CameraId}",
                Context.ConnectionId, cameraId);
        }

        public async Task UnsubscribeFromCamera(int cameraId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"camera_{cameraId}");
            _logger.LogInformation("Client {ConnectionId} unsubscribed from camera {CameraId}",
                Context.ConnectionId, cameraId);
        }

        public async Task<object> GetGlobalStatus()
        {
            var activeCount = _identityMatcher.GetActiveIdentityCount();
            return new { totalActive = activeCount };
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        public async Task SubscribeToVideoJob(Guid jobId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"video_{jobId}");
            _logger.LogInformation("Client {ConnectionId} subscribed to video job {JobId}",
                Context.ConnectionId, jobId);
        }

        public async Task UnsubscribeFromVideoJob(Guid jobId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"video_{jobId}");
            _logger.LogInformation("Client {ConnectionId} unsubscribed from video job {JobId}",
                Context.ConnectionId, jobId);
        }
    }
}
