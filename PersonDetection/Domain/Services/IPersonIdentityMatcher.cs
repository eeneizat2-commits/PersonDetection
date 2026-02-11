namespace PersonDetection.Domain.Services
{
    using PersonDetection.Domain.ValueObjects;

    public interface IPersonIdentityMatcher
    {
        Guid GetOrCreateIdentity(FeatureVector vector);
        Guid GetOrCreateIdentity(FeatureVector vector, int cameraId);
        Guid GetOrCreateIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox);
        bool TryMatch(FeatureVector vector, out Guid personId, out float similarity);
        void UpdateIdentity(Guid personId, FeatureVector vector);
        void UpdateIdentity(Guid personId, FeatureVector vector, int cameraId);
        void SetDbId(Guid personId, int dbId);
        int GetDbId(Guid personId);
        int GetActiveIdentityCount();
        int GetConfirmedIdentityCount();      // NEW
        int GetCameraIdentityCount(int cameraId);  // NEW
        void CleanupExpired(TimeSpan expirationTime);
        void ClearAllIdentities();
        void ClearCameraIdentities(int cameraId);  // NEW
    }

    public interface IDetectionValidator
    {
        bool IsValid(PersonDetection.Domain.Entities.DetectedPerson detection);
        IEnumerable<PersonDetection.Domain.Entities.DetectedPerson> FilterValid(
            IEnumerable<PersonDetection.Domain.Entities.DetectedPerson> detections);
    }
}