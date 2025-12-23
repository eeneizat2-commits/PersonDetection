// PersonDetection.Domain/Entities/PersonSighting.cs
namespace PersonDetection.Domain.Entities
{
    using PersonDetection.Domain.Common;

    public class PersonSighting : Entity
    {
        public int UniquePersonId { get; set; }
        public int CameraId { get; set; }
        public int? DetectionResultId { get; set; }
        public DateTime SeenAt { get; set; }
        public float Confidence { get; set; }
        public int BoundingBox_X { get; set; }
        public int BoundingBox_Y { get; set; }
        public int BoundingBox_Width { get; set; }
        public int BoundingBox_Height { get; set; }

        public UniquePerson? UniquePerson { get; set; }
        public DetectionResult? DetectionResult { get; set; }

        public PersonSighting() { }

        public static PersonSighting Create(int uniquePersonId, int cameraId, int? detectionResultId,
            float confidence, int x, int y, int width, int height)
        {
            return new PersonSighting
            {
                UniquePersonId = uniquePersonId,
                CameraId = cameraId,
                DetectionResultId = detectionResultId,
                SeenAt = DateTime.UtcNow,
                Confidence = confidence,
                BoundingBox_X = x,
                BoundingBox_Y = y,
                BoundingBox_Width = width,
                BoundingBox_Height = height
            };
        }
    }
}