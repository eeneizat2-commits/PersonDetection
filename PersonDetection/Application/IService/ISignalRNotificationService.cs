namespace PersonDetection.Application.IService
{
    public interface ISignalRNotificationService
    {
        Task NotifyNewVideoJob(Guid jobId, string fileName);
        Task NotifyVideoProgress(Guid jobId, string fileName, int progress, int processedFrames, int totalPersons, int uniquePersons);
        Task NotifyVideoComplete(Guid jobId, string fileName, string state, int totalPersons, int uniquePersons, double processingTime);
    }
}
