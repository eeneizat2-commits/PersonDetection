using Microsoft.AspNetCore.SignalR;
using PersonDetection.API.Hubs;
using PersonDetection.Application.IService;

namespace PersonDetection.Application.Services
{

    public class SignalRNotificationService : ISignalRNotificationService
    {
        private readonly IHubContext<DetectionHub> _hubContext;

        public SignalRNotificationService(IHubContext<DetectionHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public async Task NotifyNewVideoJob(Guid jobId, string fileName)
        {
            await _hubContext.Clients.All.SendAsync("NewVideoJobCreated", new
            {
                JobId = jobId.ToString(),
                FileName = fileName,
                State = "Queued",
                CreatedAt = DateTime.UtcNow
            });
        }

        public async Task NotifyVideoProgress(Guid jobId, string fileName, int progress, int processedFrames, int totalPersons, int uniquePersons)
        {
            await _hubContext.Clients.All.SendAsync("VideoProcessingProgress", new
            {
                JobId = jobId.ToString(),
                FileName = fileName,
                Progress = progress,
                ProcessedFrames = processedFrames,
                TotalPersonsDetected = totalPersons,
                UniquePersons = uniquePersons
            });
        }

        public async Task NotifyVideoComplete(Guid jobId, string fileName, string state, int totalPersons, int uniquePersons, double processingTime)
        {
            await _hubContext.Clients.All.SendAsync("VideoProcessingComplete", new
            {
                JobId = jobId.ToString(),
                FileName = fileName,
                State = state,
                TotalPersonsDetected = totalPersons,
                UniquePersons = uniquePersons,
                ProcessingTimeSeconds = processingTime
            });
        }
    }
}
