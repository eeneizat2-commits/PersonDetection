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
                DetectedAt = DateTime.UtcNow
            };
        }


        /// <summary>
        /// Assign identity - ONLY sets GlobalPersonId
        /// Features are managed EXCLUSIVELY by PersonIdentityService → UniquePersonFeatures table
        /// </summary>
        public void AssignIdentity(Guid globalPersonId)
        {
            GlobalPersonId = globalPersonId;
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

       
    }
}