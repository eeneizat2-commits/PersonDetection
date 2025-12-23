// PersonDetection.Domain/Entities/Camera.cs
namespace PersonDetection.Domain.Entities
{
    using PersonDetection.Domain.Common;

    public class Camera : Entity
    {
        public new int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Description { get; set; }
        public CameraType Type { get; set; } = CameraType.IP;
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastConnectedAt { get; set; }
        public int DisplayOrder { get; set; }

        public Camera()
        {
            CreatedAt = DateTime.UtcNow;
        }

        public static Camera Create(string name, string url, CameraType type = CameraType.IP, string? description = null)
        {
            return new Camera
            {
                Name = name,
                Url = url,
                Type = type,
                Description = description,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow
            };
        }

        public void UpdateLastConnected()
        {
            LastConnectedAt = DateTime.UtcNow;
        }
    }

    public enum CameraType
    {
        Webcam = 0,
        IP = 1,
        RTSP = 2,
        HTTP = 3,
        File = 4
    }
}