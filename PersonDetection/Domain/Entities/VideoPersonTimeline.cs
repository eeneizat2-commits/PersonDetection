// PersonDetection.Domain/Entities/VideoPersonTimeline.cs
namespace PersonDetection.Domain.Entities
{
    using PersonDetection.Domain.Common;

    public class VideoPersonTimeline : Entity
    {
        public int VideoJobId { get; set; }
        public Guid GlobalPersonId { get; set; }
        public int? UniquePersonId { get; set; }  // FK to UniquePersons (nullable)
        public double FirstAppearanceSeconds { get; set; }
        public double LastAppearanceSeconds { get; set; }
        public int TotalAppearances { get; set; }
        public float AverageConfidence { get; set; }
        public string? ThumbnailBase64 { get; set; }  // Store thumbnail as base64
        public string? FeatureVector { get; set; }  // Store features for re-identification
        public DateTime CreatedAt { get; set; }

        // Navigation properties
        public VideoJob? VideoJob { get; set; }
        public UniquePerson? UniquePerson { get; set; }

        public VideoPersonTimeline()
        {
            CreatedAt = DateTime.UtcNow;
        }

        public static VideoPersonTimeline Create(
            int videoJobId,
            Guid globalPersonId,
            double firstAppearance,
            double lastAppearance,
            int totalAppearances,
            float averageConfidence,
            float[]? features = null,
            byte[]? thumbnail = null)
        {
            return new VideoPersonTimeline
            {
                VideoJobId = videoJobId,
                GlobalPersonId = globalPersonId,
                FirstAppearanceSeconds = firstAppearance,
                LastAppearanceSeconds = lastAppearance,
                TotalAppearances = totalAppearances,
                AverageConfidence = averageConfidence,
                FeatureVector = features != null ? string.Join(",", features) : null,
                ThumbnailBase64 = thumbnail != null ? Convert.ToBase64String(thumbnail) : null,
                CreatedAt = DateTime.UtcNow
            };
        }

        public void SetThumbnail(byte[] thumbnailData)
        {
            ThumbnailBase64 = Convert.ToBase64String(thumbnailData);
        }

        public byte[]? GetThumbnail()
        {
            if (string.IsNullOrEmpty(ThumbnailBase64)) return null;
            return Convert.FromBase64String(ThumbnailBase64);
        }

        public void LinkToUniquePerson(int uniquePersonId)
        {
            UniquePersonId = uniquePersonId;
        }
    }
}