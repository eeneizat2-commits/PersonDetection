// PersonDetection.Domain/Services/IPersonIdentityMatcher.cs
namespace PersonDetection.Domain.Services
{
    using PersonDetection.Domain.ValueObjects;

    public interface IPersonIdentityMatcher
    {
        // Set frame dimensions for entry zone calculation
        void SetFrameDimensions(int width, int height);

        Guid GetOrCreateIdentity(FeatureVector vector);
        Guid GetOrCreateIdentity(FeatureVector vector, int cameraId);
        Guid GetOrCreateIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox);
        Guid GetOrCreateIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox, float detectionConfidence);
        Guid GetOrCreateIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox, float detectionConfidence, int trackId);

        bool TryMatch(FeatureVector vector, out Guid personId, out float similarity);

        void UpdateIdentity(Guid personId, FeatureVector vector);
        void UpdateIdentity(Guid personId, FeatureVector vector, int cameraId);

        void SetDbId(Guid personId, int dbId);
        int GetDbId(Guid personId);

        int GetActiveIdentityCount();
        int GetConfirmedIdentityCount();
        int GetCameraIdentityCount(int cameraId);
        int GetGlobalUniqueCount();
        int GetCurrentlyActiveCount(int cameraId);
        int GetTodayUniqueCount();
        int GetSessionUniqueCount();

        void CleanupExpired(TimeSpan expirationTime);
        void ClearAllIdentities();
        void ClearCameraIdentities(int cameraId);
        void StartNewSession();

        Dictionary<string, object> GetStatistics();
    }
}