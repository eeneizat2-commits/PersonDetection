// PersonDetection.Domain/Entities/DetectedPerson.cs
namespace PersonDetection.Domain.Entities
{
    using PersonDetection.Domain.Common;
    using PersonDetection.Domain.ValueObjects;

    public class DetectedPerson : Entity
    {
        public Guid GlobalPersonId { get; set; }
        public float Confidence { get; set; }
        public int BoundingBox_X { get; set; }
        public int BoundingBox_Y { get; set; }
        public int BoundingBox_Width { get; set; }
        public int BoundingBox_Height { get; set; }
        public string? FeatureVector { get; set; }
        public int? TrackId { get; set; }
        public DateTime DetectedAt { get; set; }
        public int DetectionResultId { get; set; }
        public int? VideoJobId { get; set; }  // 👈 ADD THIS - nullable FK to VideoJob
        public int? FrameNumber { get; set; }  // 👈 ADD THIS - frame number in video
        public double? TimestampSeconds { get; set; }  // 👈 ADD THIS - timestamp in video

        // Navigation properties
        public DetectionResult? DetectionResult { get; set; }
        public VideoJob? VideoJob { get; set; }  // 👈 ADD THIS

        // Non-mapped property for convenience
        public BoundingBox BoundingBox
        {
            get => new BoundingBox(BoundingBox_X, BoundingBox_Y, BoundingBox_Width, BoundingBox_Height);
            set
            {
                BoundingBox_X = value.X;
                BoundingBox_Y = value.Y;
                BoundingBox_Width = value.Width;
                BoundingBox_Height = value.Height;
            }
        }

        public DetectedPerson()
        {
            GlobalPersonId = Guid.NewGuid();
            DetectedAt = DateTime.UtcNow;
        }

        public static DetectedPerson Create(BoundingBox boundingBox, float confidence, Guid? globalPersonId = null)
        {
            if (confidence < 0 || confidence > 1)
                throw new ArgumentException("Confidence must be between 0 and 1", nameof(confidence));

            return new DetectedPerson
            {
                GlobalPersonId = globalPersonId ?? Guid.NewGuid(),
                Confidence = confidence,
                BoundingBox_X = boundingBox.X,
                BoundingBox_Y = boundingBox.Y,
                BoundingBox_Width = boundingBox.Width,
                BoundingBox_Height = boundingBox.Height,
                DetectedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Create for video frame detection
        /// </summary>
        public static DetectedPerson CreateForVideo(
            BoundingBox boundingBox,
            float confidence,
            Guid globalPersonId,
            int videoJobId,
            int frameNumber,
            double timestampSeconds,
            float[]? features = null)
        {
            return new DetectedPerson
            {
                GlobalPersonId = globalPersonId,
                Confidence = confidence,
                BoundingBox_X = boundingBox.X,
                BoundingBox_Y = boundingBox.Y,
                BoundingBox_Width = boundingBox.Width,
                BoundingBox_Height = boundingBox.Height,
                VideoJobId = videoJobId,
                FrameNumber = frameNumber,
                TimestampSeconds = timestampSeconds,
                FeatureVector = features != null ? string.Join(",", features) : null,
                DetectedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Assign identity with FeatureVector object
        /// </summary>
        public void AssignIdentity(Guid globalPersonId, FeatureVector featureVector)
        {
            GlobalPersonId = globalPersonId;
            FeatureVector = featureVector != null ? string.Join(",", featureVector.Values) : null;
        }

        /// <summary>
        /// Assign identity with float array
        /// </summary>
        public void AssignIdentity(Guid globalPersonId, float[]? features)
        {
            GlobalPersonId = globalPersonId;
            FeatureVector = features != null ? string.Join(",", features) : null;
        }

        public void UpdateTrackId(int trackId)
        {
            TrackId = trackId;
        }

        /// <summary>
        /// Check if detection meets minimum quality requirements
        /// </summary>
        public bool MeetsMinimumQuality(float minConfidence, int minWidth, int minHeight, float minAspectRatio)
        {
            var aspectRatio = BoundingBox_Width > 0 ? (float)BoundingBox_Height / BoundingBox_Width : 0;

            return Confidence >= minConfidence &&
                   BoundingBox_Width >= minWidth &&
                   BoundingBox_Height >= minHeight &&
                   aspectRatio >= minAspectRatio;
        }

        /// <summary>
        /// Get feature vector as float array
        /// </summary>
        public float[]? GetFeatureArray()
        {
            if (string.IsNullOrEmpty(FeatureVector)) return null;
            try
            {
                return FeatureVector.Split(',').Select(float.Parse).ToArray();
            }
            catch
            {
                return null;
            }
        }
    }
}