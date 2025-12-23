namespace PersonDetection.Application.DTOs
{
    public record VideoUploadResultDto(
        Guid JobId,
        string FileName,
        string Status,
        DateTime UploadedAt,
        string Message);

    public record VideoProcessingStatusDto(
        Guid JobId,
        string FileName,
        VideoProcessingState State,
        int TotalFrames,
        int ProcessedFrames,
        int ProgressPercent,
        int TotalPersonsDetected,
        int UniquePersonsDetected,
        DateTime StartedAt,
        DateTime? CompletedAt,
        string? ErrorMessage,
        List<VideoFrameDetectionDto> Detections);

    public record VideoFrameDetectionDto(
        int FrameNumber,
        double TimestampSeconds,
        int PersonCount,
        List<PersonDetectionDto> Persons);

    public record VideoProcessingSummaryDto(
        Guid JobId,
        string FileName,
        TimeSpan VideoDuration,
        int TotalFramesProcessed,
        int TotalPersonsDetected,
        int UniquePersonsIdentified,
        double AveragePersonsPerFrame,
        int PeakPersonCount,
        double ProcessingTimeSeconds,
        List<PersonTimelineDto> PersonTimelines);

    public record PersonTimelineDto(
        Guid GlobalPersonId,
        string ShortId,
        double FirstAppearanceSeconds,
        double LastAppearanceSeconds,
        int TotalAppearances,
        float AverageConfidence);

    public enum VideoProcessingState
    {
        Queued = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4
    }
}