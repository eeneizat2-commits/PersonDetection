namespace PersonDetection.Domain.Entities
{
    using global::PersonDetection.Domain.Common;
    using global::PersonDetection.Domain.Events;

    public class CameraSession : Entity
    {
        public int CameraId { get; private set; }
        public string Url { get; private set; }
        public bool IsActive { get; private set; }
        public DateTime StartedAt { get; private set; }
        public DateTime? StoppedAt { get; private set; }
        public TimeSpan? Duration => StoppedAt.HasValue ? StoppedAt.Value - StartedAt : null;

        private CameraSession() { } // EF Core

        public static CameraSession Start(int cameraId, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be empty", nameof(url));

            var session = new CameraSession
            {
                CameraId = cameraId,
                Url = url,
                IsActive = true,
                StartedAt = DateTime.UtcNow
            };

            session.AddDomainEvent(new CameraStartedEvent(cameraId, url, DateTime.UtcNow));
            return session;
        }

        public void Stop()
        {
            if (!IsActive)
                throw new InvalidOperationException("Camera session is already stopped");

            IsActive = false;
            StoppedAt = DateTime.UtcNow;
            AddDomainEvent(new CameraStoppedEvent(CameraId, DateTime.UtcNow));
        }
    }
}