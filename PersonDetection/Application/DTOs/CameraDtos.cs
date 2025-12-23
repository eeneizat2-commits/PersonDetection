// PersonDetection.Application/DTOs/CameraDtos.cs
namespace PersonDetection.Application.DTOs
{
    using PersonDetection.Domain.Entities;

    public record CameraDto(
        int Id,
        string Name,
        string Url,
        string? Description,
        CameraType Type,
        bool IsEnabled,
        DateTime CreatedAt,
        DateTime? LastConnectedAt,
        int DisplayOrder,
        bool IsActive = false);

    public record CreateCameraRequest(
        string Name,
        string Url,
        string? Description = null,
        CameraType Type = CameraType.IP);

    public record UpdateCameraRequest(
        string Name,
        string Url,
        string? Description,
        CameraType Type,
        bool IsEnabled,
        int DisplayOrder);
}