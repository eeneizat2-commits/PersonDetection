// PersonDetection.Domain/Entities/DetectionResult.cs
namespace PersonDetection.Domain.Entities
{
    using PersonDetection.Domain.Common;
    using PersonDetection.Domain.Events;

    public class DetectionResult : Entity
    {
        private readonly List<DetectedPerson> _detections = new();

        public int CameraId { get; set; }
        public int? VideoJobId { get; set; }  // 👈 ADD THIS - nullable FK to VideoJob
        public DateTime Timestamp { get; set; }
        public int TotalDetections { get; set; }
        public int ValidDetections { get; set; }
        public int UniquePersonCount { get; set; }

        public IReadOnlyCollection<DetectedPerson> Detections => _detections.AsReadOnly();
        public VideoJob? VideoJob { get; set; }  // 👈 ADD THIS

        public DetectionResult()
        {
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Create with counts only (no detections list)
        /// </summary>
        public static DetectionResult Create(int cameraId, int totalDetections, int validDetections, int uniqueCount)
        {
            return new DetectionResult
            {
                CameraId = cameraId,
                Timestamp = DateTime.UtcNow,
                TotalDetections = totalDetections,
                ValidDetections = validDetections,
                UniquePersonCount = uniqueCount
            };
        }

        /// <summary>
        /// Create for video processing
        /// </summary>
        public static DetectionResult CreateForVideo(int videoJobId, int frameNumber, double fps, int totalDetections, int validDetections, int uniqueCount)
        {
            return new DetectionResult
            {
                CameraId = 0,  // Use 0 for video uploads
                VideoJobId = videoJobId,
                Timestamp = DateTime.UtcNow,
                TotalDetections = totalDetections,
                ValidDetections = validDetections,
                UniquePersonCount = uniqueCount
            };
        }

        /// <summary>
        /// Create from a list of DetectedPerson entities
        /// </summary>
        public static DetectionResult Create(int cameraId, IEnumerable<DetectedPerson> detections)
        {
            var detectionList = detections.ToList();
            var result = new DetectionResult
            {
                CameraId = cameraId,
                Timestamp = DateTime.UtcNow,
                TotalDetections = detectionList.Count,
                ValidDetections = detectionList.Count(d => d.Confidence >= 0.4f),
                UniquePersonCount = detectionList.Select(d => d.GlobalPersonId).Distinct().Count()
            };

            result._detections.AddRange(detectionList);
            result.AddDomainEvent(new PersonsDetectedEvent(cameraId, detectionList.Count, DateTime.UtcNow));

            return result;
        }

        /// <summary>
        /// Create from a list with custom confidence threshold
        /// </summary>
        public static DetectionResult Create(int cameraId, IEnumerable<DetectedPerson> detections, float confidenceThreshold)
        {
            var detectionList = detections.ToList();
            var result = new DetectionResult
            {
                CameraId = cameraId,
                Timestamp = DateTime.UtcNow,
                TotalDetections = detectionList.Count,
                ValidDetections = detectionList.Count(d => d.Confidence >= confidenceThreshold),
                UniquePersonCount = detectionList.Select(d => d.GlobalPersonId).Distinct().Count()
            };

            result._detections.AddRange(detectionList);
            return result;
        }

        public void AddDetection(DetectedPerson person)
        {
            _detections.Add(person);
            TotalDetections = _detections.Count;
            ValidDetections = _detections.Count(d => d.Confidence >= 0.4f);
            UniquePersonCount = _detections.Select(d => d.GlobalPersonId).Distinct().Count();
        }

        public void FilterByQuality(float minConfidence, int minWidth, int minHeight, float minAspectRatio)
        {
            _detections.RemoveAll(d => !d.MeetsMinimumQuality(minConfidence, minWidth, minHeight, minAspectRatio));
            ValidDetections = _detections.Count;
        }

        public IEnumerable<DetectedPerson> GetUniquePersons()
        {
            return _detections
                .GroupBy(d => d.GlobalPersonId)
                .Select(g => g.OrderByDescending(d => d.Confidence).First());
        }
    }
}