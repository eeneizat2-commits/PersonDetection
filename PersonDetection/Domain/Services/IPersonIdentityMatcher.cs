namespace PersonDetection.Domain.Services
{
    using PersonDetection.Domain.ValueObjects;

    public interface IPersonIdentityMatcher
    {
        /// <summary>
        /// Get or create identity from feature vector
        /// </summary>
        Guid GetOrCreateIdentity(FeatureVector vector);

        /// <summary>
        /// Get or create identity with camera context
        /// </summary>
        Guid GetOrCreateIdentity(FeatureVector vector, int cameraId);

        /// <summary>
        /// Try to match a feature vector to existing identity
        /// </summary>
        bool TryMatch(FeatureVector vector, out Guid personId, out float similarity);

        /// <summary>
        /// Update an existing identity's feature vector
        /// </summary>
        void UpdateIdentity(Guid personId, FeatureVector vector);

        /// <summary>
        /// Update identity with camera context
        /// </summary>
        void UpdateIdentity(Guid personId, FeatureVector vector, int cameraId);

        /// <summary>
        /// Set the database ID for a person
        /// </summary>
        void SetDbId(Guid personId, int dbId);

        /// <summary>
        /// Get the database ID for a person
        /// </summary>
        int GetDbId(Guid personId);

        /// <summary>
        /// Get count of active identities in memory
        /// </summary>
        int GetActiveIdentityCount();

        /// <summary>
        /// Clean up expired identities
        /// </summary>
        void CleanupExpired(TimeSpan expirationTime);
    }
    public interface IDetectionValidator
    {
        bool IsValid(PersonDetection.Domain.Entities.DetectedPerson detection);
        IEnumerable<PersonDetection.Domain.Entities.DetectedPerson> FilterValid(
            IEnumerable<PersonDetection.Domain.Entities.DetectedPerson> detections);
    }
}