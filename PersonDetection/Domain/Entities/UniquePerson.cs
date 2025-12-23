// PersonDetection.Domain/Entities/UniquePerson.cs
namespace PersonDetection.Domain.Entities
{
    using PersonDetection.Domain.Common;
    using PersonDetection.Domain.ValueObjects;

    public class UniquePerson : Entity
    {
        public Guid GlobalPersonId { get; set; }
        public DateTime FirstSeenAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public int FirstSeenCameraId { get; set; }
        public int LastSeenCameraId { get; set; }
        public int TotalSightings { get; set; } = 1;
        public string? FeatureVector { get; set; }
        public byte[]? ThumbnailData { get; set; }
        public string? Label { get; set; }
        public bool IsActive { get; set; } = true;

        private readonly List<PersonSighting> _sightings = new();
        public IReadOnlyCollection<PersonSighting> Sightings => _sightings.AsReadOnly();

        public UniquePerson() { }

        public static UniquePerson Create(Guid globalPersonId, int cameraId, float[]? features = null, byte[]? thumbnail = null)
        {
            var now = DateTime.UtcNow;
            return new UniquePerson
            {
                GlobalPersonId = globalPersonId,
                FirstSeenAt = now,
                LastSeenAt = now,
                FirstSeenCameraId = cameraId,
                LastSeenCameraId = cameraId,
                TotalSightings = 1,
                FeatureVector = features != null ? string.Join(",", features) : null,
                ThumbnailData = thumbnail,
                IsActive = true
            };
        }

        public void UpdateLastSeen(int cameraId, float[]? newFeatures = null)
        {
            LastSeenAt = DateTime.UtcNow;
            LastSeenCameraId = cameraId;
            TotalSightings++;

            if (newFeatures != null)
            {
                // Update feature vector with exponential moving average
                FeatureVector = string.Join(",", newFeatures);
            }
        }

        public float[]? GetFeatureArray()
        {
            if (string.IsNullOrEmpty(FeatureVector)) return null;
            return FeatureVector.Split(',').Select(float.Parse).ToArray();
        }
    }
}