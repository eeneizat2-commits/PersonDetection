// PersonDetection.Infrastructure/Identity/PersonIdentityService.cs
namespace PersonDetection.Infrastructure.Identity
{
    using System.Collections.Concurrent;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Services;
    using PersonDetection.Domain.ValueObjects;
    using PersonDetection.Infrastructure.Context;

    public class PersonIdentityService : IPersonIdentityMatcher
    {
        private readonly ConcurrentDictionary<Guid, PersonIdentity> _identities = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly IdentitySettings _settings;
        private readonly ILogger<PersonIdentityService> _logger;

        // Store ORIGINAL feature vectors - NEVER update them
        private readonly ConcurrentDictionary<Guid, float[]> _originalFeatures = new();

        private class PersonIdentity
        {
            public Guid GlobalPersonId { get; set; }
            public int DbId { get; set; }
            public float[] OriginalFeatureVector { get; set; } = null!;  // NEVER changes
            public DateTime LastSeen { get; set; }
            public DateTime FirstSeen { get; set; }
            public int CameraId { get; set; }
            public int MatchCount { get; set; } = 1;
            public BoundingBox? LastBoundingBox { get; set; }
        }

        public PersonIdentityService(
            IServiceProvider serviceProvider,
            IdentitySettings settings,
            ILogger<PersonIdentityService> logger)
        {
            _serviceProvider = serviceProvider;
            _settings = settings;
            _logger = logger;

            _logger.LogWarning("🚀 PersonIdentityService initialized with DistanceThreshold={Threshold}, RequireSeparation={Sep}",
                _settings.DistanceThreshold, _settings.RequireMinimumSeparation);

            Task.Run(LoadExistingIdentities);
        }

        // Backward compatible constructor
        public PersonIdentityService(
            IServiceProvider serviceProvider,
            float distanceThreshold,
            bool updateVectorOnMatch,
            ILogger<PersonIdentityService> logger)
            : this(serviceProvider, new IdentitySettings
            {
                DistanceThreshold = distanceThreshold,
                UpdateVectorOnMatch = false  // ALWAYS false now
            }, logger)
        {
        }

        private async Task LoadExistingIdentities()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                // Only load from CURRENT session (last 30 minutes)
                var recentPersons = await context.UniquePersons
                    .Where(p => p.IsActive && p.LastSeenAt >= DateTime.UtcNow.AddMinutes(-30))
                    .ToListAsync();

                foreach (var person in recentPersons)
                {
                    var features = person.GetFeatureArray();
                    if (features != null && features.Length == 512)
                    {
                        var identity = new PersonIdentity
                        {
                            GlobalPersonId = person.GlobalPersonId,
                            DbId = person.Id,
                            OriginalFeatureVector = features,
                            LastSeen = person.LastSeenAt,
                            FirstSeen = person.FirstSeenAt,
                            CameraId = person.LastSeenCameraId,
                            MatchCount = person.TotalSightings
                        };

                        _identities[person.GlobalPersonId] = identity;
                        _originalFeatures[person.GlobalPersonId] = features;
                    }
                }

                _logger.LogInformation("✅ Loaded {Count} recent identities from database", _identities.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load existing identities");
            }
        }

        public Guid GetOrCreateIdentity(FeatureVector vector)
        {
            return GetOrCreateIdentity(vector, 0, null);
        }

        public Guid GetOrCreateIdentity(FeatureVector vector, int cameraId)
        {
            return GetOrCreateIdentity(vector, cameraId, null);
        }

        public Guid GetOrCreateIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox)
        {
            // Validate feature vector
            if (!IsValidFeatureVector(vector))
            {
                _logger.LogWarning("⚠️ Invalid feature vector - creating temp ID");
                return Guid.NewGuid();
            }

            // Try to match with strict criteria
            var matchResult = TryMatchStrict(vector, cameraId, boundingBox);

            if (matchResult.IsMatch)
            {
                // Update ONLY metadata, NEVER the feature vector
                UpdateMetadataOnly(matchResult.PersonId, cameraId, boundingBox);
                return matchResult.PersonId;
            }

            // Create new identity with ORIGINAL features (never to be modified)
            return CreateNewIdentity(vector, cameraId, boundingBox);
        }

        private bool IsValidFeatureVector(FeatureVector vector)
        {
            var values = vector.Values;

            if (values == null || values.Length != 512)
                return false;

            if (values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                return false;

            // Check for degenerate vectors (all same value)
            var uniqueValues = values.Distinct().Count();
            if (uniqueValues < 100)
            {
                _logger.LogWarning("⚠️ Degenerate feature vector with only {Count} unique values", uniqueValues);
                return false;
            }

            return true;
        }

        private (bool IsMatch, Guid PersonId, float Distance) TryMatchStrict(
            FeatureVector vector,
            int cameraId,
            BoundingBox? boundingBox)
        {
            if (_identities.IsEmpty)
            {
                _logger.LogDebug("🔍 No existing identities - will create new");
                return (false, Guid.Empty, float.MaxValue);
            }

            // Calculate distances to ALL identities
            var distances = new List<(Guid id, float distance)>();

            foreach (var (id, identity) in _identities)
            {
                // Use ORIGINAL features only
                var storedVector = new FeatureVector(identity.OriginalFeatureVector);
                var distance = vector.EuclideanDistance(storedVector);
                distances.Add((id, distance));
            }

            // Sort by distance
            distances = distances.OrderBy(d => d.distance).ToList();

            var best = distances[0];
            var secondBest = distances.Count > 1 ? distances[1] : (id: Guid.Empty, distance: float.MaxValue);


            _logger.LogInformation(
                "🔍 Distances: Best={BestId}:{BestDist:F4}, Second={SecondId}:{SecondDist:F4}, Threshold={Thresh:F4}",
                best.id.ToString()[..8],
                best.distance,
                secondBest.id != Guid.Empty ? secondBest.id.ToString()[..8] : "none",
                secondBest.distance,
                _settings.DistanceThreshold);

            // Rule 1: Best distance must be below threshold
            if (best.distance > _settings.DistanceThreshold)
            {
                _logger.LogInformation("❌ NO MATCH: Best distance {Dist:F4} > threshold {Thresh:F4}",
                    best.distance, _settings.DistanceThreshold);
                return (false, Guid.Empty, best.distance);
            }

            // Rule 2: CRITICAL - Require minimum separation from second best
            if (_settings.RequireMinimumSeparation && secondBest.id != Guid.Empty)
            {
                var separationRatio = secondBest.distance / Math.Max(best.distance, 0.001f);

                _logger.LogDebug("📊 Separation ratio: {Ratio:F2} (required: {Required:F2})",
                    separationRatio, _settings.MinSeparationRatio);

                if (separationRatio < _settings.MinSeparationRatio)
                {
                    _logger.LogWarning(
                        "⚠️ AMBIGUOUS MATCH REJECTED: Best={BestDist:F4}, Second={SecondDist:F4}, Ratio={Ratio:F2}",
                        best.distance, secondBest.distance, separationRatio);
                    return (false, Guid.Empty, best.distance);
                }
            }

            // Rule 3: If distance is very small (<0.05), require spatial consistency
            if (best.distance < 0.05f && boundingBox != null)
            {
                if (_identities.TryGetValue(best.id, out var identity) && identity.LastBoundingBox != null)
                {
                    var timeDelta = (DateTime.UtcNow - identity.LastSeen).TotalSeconds;
                    if (timeDelta < 2.0)
                    {
                        var spatialDist = CalculateCenterDistance(boundingBox, identity.LastBoundingBox);
                        var maxMovement = 200 * timeDelta; // Max 200px/second movement

                        if (spatialDist > maxMovement)
                        {
                            _logger.LogWarning(
                                "⚠️ SPATIAL INCONSISTENCY: Dist={Dist:F4} but moved {Spatial:F0}px in {Time:F1}s",
                                best.distance, spatialDist, timeDelta);
                            return (false, Guid.Empty, best.distance);
                        }
                    }
                }
            }

            _logger.LogInformation("✅ MATCHED: {Id} (dist={Dist:F4})", best.id.ToString()[..8], best.distance);
            return (true, best.id, best.distance);
        }

        private float CalculateCenterDistance(BoundingBox a, BoundingBox b)
        {
            var ax = a.X + a.Width / 2f;
            var ay = a.Y + a.Height / 2f;
            var bx = b.X + b.Width / 2f;
            var by = b.Y + b.Height / 2f;

            return (float)Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        }

        private void UpdateMetadataOnly(Guid personId, int cameraId, BoundingBox? boundingBox)
        {
            if (!_identities.TryGetValue(personId, out var identity))
                return;

            // Update ONLY metadata - NEVER touch the feature vector!
            identity.LastSeen = DateTime.UtcNow;
            identity.LastBoundingBox = boundingBox;
            if (cameraId > 0) identity.CameraId = cameraId;
            identity.MatchCount++;
        }

        private Guid CreateNewIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox)
        {
            var newId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            // Store ORIGINAL features - these will NEVER be modified
            var originalFeatures = (float[])vector.Values.Clone();

            var identity = new PersonIdentity
            {
                GlobalPersonId = newId,
                DbId = 0,
                OriginalFeatureVector = originalFeatures,
                LastSeen = now,
                FirstSeen = now,
                CameraId = cameraId,
                MatchCount = 1,
                LastBoundingBox = boundingBox
            };

            _identities[newId] = identity;
            _originalFeatures[newId] = originalFeatures;

            _logger.LogWarning("🆕 NEW PERSON: {Id} (total identities: {Total})",
                newId.ToString()[..8], _identities.Count);

            return newId;
        }

        public bool TryMatch(FeatureVector vector, out Guid personId, out float similarity)
        {
            var result = TryMatchStrict(vector, 0, null);
            personId = result.PersonId;
            similarity = 1f / (1f + result.Distance);
            return result.IsMatch;
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector)
        {
            // DO NOTHING - we never update feature vectors
            _logger.LogDebug("UpdateIdentity called but ignored (vector updates disabled)");
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector, int cameraId)
        {
            // Only update metadata
            UpdateMetadataOnly(personId, cameraId, null);
        }

        public void SetDbId(Guid personId, int dbId)
        {
            if (_identities.TryGetValue(personId, out var identity))
            {
                identity.DbId = dbId;
            }
        }

        public int GetDbId(Guid personId)
        {
            return _identities.TryGetValue(personId, out var identity) ? identity.DbId : 0;
        }

        public int GetActiveIdentityCount() => _identities.Count;

        public void CleanupExpired(TimeSpan expirationTime)
        {
            var threshold = DateTime.UtcNow - expirationTime;
            var expired = _identities
                .Where(kvp => kvp.Value.LastSeen < threshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in expired)
            {
                _identities.TryRemove(id, out _);
                _originalFeatures.TryRemove(id, out _);
            }

            if (expired.Count > 0)
            {
                _logger.LogInformation("🧹 Cleaned up {Count} expired identities", expired.Count);
            }
        }

        /// <summary>
        /// Clear all identities - useful for resetting between sessions
        /// </summary>
        public void ClearAllIdentities()
        {
            var count = _identities.Count;
            _identities.Clear();
            _originalFeatures.Clear();
            _logger.LogWarning("🗑️ Cleared all {Count} identities", count);
        }
    }

    public struct PointF
    {
        public float X { get; set; }
        public float Y { get; set; }

        public PointF(float x, float y)
        {
            X = x;
            Y = y;
        }
    }
}