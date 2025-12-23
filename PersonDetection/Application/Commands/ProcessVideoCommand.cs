namespace PersonDetection.Application.Commands
{
    using Microsoft.Extensions.Logging;
    using PersonDetection.Application.Common;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Application.Interfaces;

    public record ProcessVideoCommand(
        Guid JobId,
        string FilePath,
        string FileName,
        int FrameSkip = 5,
        bool ExtractFeatures = true) : ICommand<VideoUploadResultDto>;

    public class ProcessVideoHandler : ICommandHandler<ProcessVideoCommand, VideoUploadResultDto>
    {
        private readonly IVideoProcessingService _videoService;
        private readonly ILogger<ProcessVideoHandler> _logger;

        public ProcessVideoHandler(
            IVideoProcessingService videoService,
            ILogger<ProcessVideoHandler> logger)
        {
            _videoService = videoService;
            _logger = logger;
        }

        public async Task<VideoUploadResultDto> Handle(ProcessVideoCommand cmd, CancellationToken ct)
        {
            _logger.LogInformation("Queueing video processing job {JobId}: {FileName}", cmd.JobId, cmd.FileName);

            await _videoService.QueueVideoAsync(cmd.JobId, cmd.FilePath, cmd.FileName, cmd.FrameSkip, cmd.ExtractFeatures, ct);

            return new VideoUploadResultDto(
                cmd.JobId,
                cmd.FileName,
                "Queued",
                DateTime.UtcNow,
                "Video queued for processing. Use the job ID to check status.");
        }
    }
}