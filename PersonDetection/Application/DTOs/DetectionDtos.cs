// PersonDetection.Application/DTOs/DetectionDtos.cs
namespace PersonDetection.Application.DTOs
{
    public record BoundingBoxDto(int X, int Y, int Width, int Height, float AspectRatio);

    public record PersonDetectionDto(
        int Id,
        BoundingBoxDto BoundingBox,
        float Confidence,
        Guid GlobalPersonId,
        int? TrackId,
        DateTime DetectedAt);

    public record DetectionResultDto(
        int Id,
        int CameraId,
        DateTime Timestamp,
        int TotalDetections,
        int ValidDetections,
        List<PersonDetectionDto> Persons);

    public record CameraStatsDto(
        int CameraId,
        int CurrentCount,
        int UniqueCount,
        int TotalDetectionsToday,
        List<DetectionResultDto> RecentDetections);

    public record ActiveCamerasDto(
        int TotalActiveCameras,
        int TotalPersonsDetected,
        int TotalUniquePersons,
        Dictionary<int, int> CountByCamera);

    // New DTOs for unique person tracking
    public record UniquePersonDto(
        int Id,
        Guid GlobalPersonId,
        DateTime FirstSeenAt,
        DateTime LastSeenAt,
        int FirstSeenCameraId,
        int LastSeenCameraId,
        int TotalSightings,
        string? Label,
        bool IsActive);

    public record PersonSightingDto(
        int Id,
        int UniquePersonId,
        int CameraId,
        DateTime SeenAt,
        float Confidence,
        BoundingBoxDto BoundingBox);
}