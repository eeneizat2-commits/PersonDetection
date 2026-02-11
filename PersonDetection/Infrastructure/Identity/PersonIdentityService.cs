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
        private readonly ConcurrentDictionary<int, HashSet<Guid>> _cameraIdentities = new(); // Per-camera tracking
        private readonly IServiceProvider _serviceProvider;
        private readonly IdentitySettings _settings;
        private readonly ILogger<PersonIdentityService> _logger;
        private DateTime _lastConsolidation = DateTime.UtcNow;

        private class PersonIdentity
        {
            public Guid GlobalPersonId { get; set; }
            public int DbId { get; set; }
            public float[] OriginalFeatureVector { get; set; } = null!;
            public DateTime LastSeen { get; set; }
            public DateTime FirstSeen { get; set; }
            public int CameraId { get; set; }
            public int MatchCount { get; set; } = 1;
            public BoundingBox? LastBoundingBox { get; set; }
            public bool IsConfirmed { get; set; } = false;  // Only count confirmed identities
        }

        public PersonIdentityService(
            IServiceProvider serviceProvider,
            IdentitySettings settings,
            ILogger<PersonIdentityService> logger)
        {
            _serviceProvider = serviceProvider;
            _settings = settings;
            _logger = logger;

            _logger.LogWarning(
                "🚀 PersonIdentityService: DistThresh={Dist}, MinNewDist={MinNew}, MaxPerCam={Max}, Consolidation={Con}",
                _settings.DistanceThreshold,
                _settings.MinDistanceForNewIdentity,
                _settings.MaxIdentitiesPerCamera,
                _settings.EnableIdentityConsolidation);
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
                UpdateVectorOnMatch = false
            }, logger)
        {
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
            // Check if bounding box is too small for reliable ReID
            if (boundingBox != null && !IsSufficientSizeForReId(boundingBox))
            {
                _logger.LogDebug("📏 Crop too small ({W}x{H}) - using spatial tracking only",
                    boundingBox.Width, boundingBox.Height);
                return Guid.Empty; // Signal to use spatial tracking
            }

            // Validate feature vector
            if (!IsValidFeatureVector(vector))
            {
                _logger.LogWarning("⚠️ Invalid feature vector");
                return Guid.Empty;
            }

            // Periodic consolidation
            if (_settings.EnableIdentityConsolidation &&
                (DateTime.UtcNow - _lastConsolidation).TotalSeconds > 10)
            {
                ConsolidateSimilarIdentities(cameraId);
                _lastConsolidation = DateTime.UtcNow;
            }

            // Get active identities for this camera (time-windowed)
            var activeIdentities = GetActiveIdentities(cameraId);

            // Try to match
            var matchResult = TryMatchStrict(vector, activeIdentities, cameraId, boundingBox);

            if (matchResult.IsMatch)
            {
                UpdateMetadataOnly(matchResult.PersonId, cameraId, boundingBox);
                return matchResult.PersonId;
            }

            // Check if we should create new identity
            if (matchResult.BestDistance < _settings.MinDistanceForNewIdentity)
            {
                // Distance is very small - probably same person, force match to closest
                _logger.LogWarning("🔗 Distance {Dist:F4} < MinNew {Min:F4} - forcing match to {Id}",
                    matchResult.BestDistance, _settings.MinDistanceForNewIdentity,
                    matchResult.BestMatchId.ToString()[..8]);

                UpdateMetadataOnly(matchResult.BestMatchId, cameraId, boundingBox);
                return matchResult.BestMatchId;
            }

            // Check identity limit
            if (!_cameraIdentities.TryGetValue(cameraId, out var camIds))
            {
                camIds = new HashSet<Guid>();
                _cameraIdentities[cameraId] = camIds;
            }

            if (camIds.Count >= _settings.MaxIdentitiesPerCamera)
            {
                // Force match to closest identity
                if (matchResult.BestMatchId != Guid.Empty)
                {
                    _logger.LogWarning("🚫 Max identities ({Max}) reached - forcing match to closest",
                        _settings.MaxIdentitiesPerCamera);
                    UpdateMetadataOnly(matchResult.BestMatchId, cameraId, boundingBox);
                    return matchResult.BestMatchId;
                }
            }

            // Create new identity
            return CreateNewIdentity(vector, cameraId, boundingBox);
        }

        private bool IsSufficientSizeForReId(BoundingBox box)
        {
            return box.Width >= _settings.MinCropWidth &&
                   box.Height >= _settings.MinCropHeight;
        }

        private bool IsValidFeatureVector(FeatureVector vector)
        {
            var values = vector.Values;

            if (values == null || values.Length != 512)
                return false;

            if (values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                return false;

            // Check for degenerate vectors
            var variance = CalculateVariance(values);
            if (variance < 0.001f)
            {
                _logger.LogWarning("⚠️ Low variance feature vector: {Var:F6}", variance);
                return false;
            }

            return true;
        }

        private float CalculateVariance(float[] values)
        {
            var mean = values.Average();
            return values.Select(v => (v - mean) * (v - mean)).Average();
        }

        private List<PersonIdentity> GetActiveIdentities(int cameraId)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-_settings.TemporalWindowSeconds);

            // Get identities from this camera that were seen recently
            return _identities.Values
                .Where(i => i.CameraId == cameraId && i.LastSeen >= cutoff)
                .OrderByDescending(i => i.LastSeen)
                .Take(_settings.MaxIdentitiesPerCamera)
                .ToList();
        }

        private (bool IsMatch, Guid PersonId, float BestDistance, Guid BestMatchId) TryMatchStrict(
            FeatureVector vector,
            List<PersonIdentity> activeIdentities,
            int cameraId,
            BoundingBox? boundingBox)
        {
            if (activeIdentities.Count == 0)
            {
                _logger.LogDebug("🔍 No active identities for camera {Cam}", cameraId);
                return (false, Guid.Empty, float.MaxValue, Guid.Empty);
            }

            // Calculate distances
            var distances = activeIdentities
                .Select(identity => {
                    var storedVector = new FeatureVector(identity.OriginalFeatureVector);
                    var distance = vector.EuclideanDistance(storedVector);
                    return (identity.GlobalPersonId, distance, identity);
                })
                .OrderBy(d => d.distance)
                .ToList();

            var best = distances[0];
            var secondBest = distances.Count > 1
                ? distances[1]
                : (GlobalPersonId: Guid.Empty, distance: float.MaxValue, identity: (PersonIdentity?)null);

            // Log top matches
            _logger.LogInformation(
                "🔍 Camera {Cam}: Best={BestId}:{BestDist:F4}, Second={SecondDist:F4}, Active={Count}",
                cameraId,
                best.GlobalPersonId.ToString()[..8],
                best.distance,
                secondBest.distance,
                activeIdentities.Count);

            // Rule 1: Must be below threshold
            if (best.distance > _settings.DistanceThreshold)
            {
                _logger.LogInformation("❌ NO MATCH: {Dist:F4} > threshold {Thresh:F4}",
                    best.distance, _settings.DistanceThreshold);
                return (false, Guid.Empty, best.distance, best.GlobalPersonId);
            }

            // Rule 2: Check separation (only if second best exists and is close)
            if (_settings.RequireMinimumSeparation &&
                secondBest.GlobalPersonId != Guid.Empty &&
                secondBest.distance < _settings.DistanceThreshold)
            {
                var ratio = secondBest.distance / Math.Max(best.distance, 0.001f);

                if (ratio < _settings.MinSeparationRatio)
                {
                    // AMBIGUOUS - but don't create new identity if distances are very small
                    if (best.distance < _settings.MinDistanceForNewIdentity)
                    {
                        _logger.LogWarning(
                            "⚠️ Ambiguous but very close ({Dist:F4}) - accepting best match",
                            best.distance);
                        return (true, best.GlobalPersonId, best.distance, best.GlobalPersonId);
                    }

                    _logger.LogWarning(
                        "⚠️ AMBIGUOUS: Best={BestDist:F4}, Second={SecondDist:F4}, Ratio={Ratio:F2}",
                        best.distance, secondBest.distance, ratio);
                    return (false, Guid.Empty, best.distance, best.GlobalPersonId);
                }
            }

            // Rule 3: Spatial consistency check for very close matches
            if (best.distance < 0.05f && boundingBox != null && best.identity.LastBoundingBox != null)
            {
                var timeDelta = (DateTime.UtcNow - best.identity.LastSeen).TotalSeconds;
                if (timeDelta > 0 && timeDelta < 2.0)
                {
                    var spatialDist = CalculateCenterDistance(boundingBox, best.identity.LastBoundingBox);
                    var maxMovement = 300 * timeDelta; // Allow faster movement

                    if (spatialDist > maxMovement)
                    {
                        _logger.LogDebug(
                            "📍 Spatial check: moved {Dist:F0}px in {Time:F1}s (max: {Max:F0})",
                            spatialDist, timeDelta, maxMovement);
                        // Don't reject, just note it
                    }
                }
            }

            _logger.LogInformation("✅ MATCHED: {Id} (dist={Dist:F4}, matches={Count})",
                best.GlobalPersonId.ToString()[..8], best.distance, best.identity.MatchCount + 1);

            return (true, best.GlobalPersonId, best.distance, best.GlobalPersonId);
        }

        private float CalculateCenterDistance(BoundingBox a, BoundingBox b)
        {
            var ax = a.X + a.Width / 2f;
            var ay = a.Y + a.Height / 2f;
            var bx = b.X + b.Width / 2f;
            var by = b.Y + b.Height / 2f;

            return (float)Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        }

        private void ConsolidateSimilarIdentities(int cameraId)
        {
            if (!_cameraIdentities.TryGetValue(cameraId, out var camIds) || camIds.Count < 2)
                return;

            var identities = camIds
                .Where(id => _identities.ContainsKey(id))
                .Select(id => _identities[id])
                .ToList();

            var toMerge = new List<(Guid keep, Guid remove)>();

            for (int i = 0; i < identities.Count; i++)
            {
                for (int j = i + 1; j < identities.Count; j++)
                {
                    var vecA = new FeatureVector(identities[i].OriginalFeatureVector);
                    var vecB = new FeatureVector(identities[j].OriginalFeatureVector);
                    var distance = vecA.EuclideanDistance(vecB);

                    if (distance < _settings.ConsolidationThreshold)
                    {
                        // Keep the one with more matches
                        var keep = identities[i].MatchCount >= identities[j].MatchCount
                            ? identities[i].GlobalPersonId
                            : identities[j].GlobalPersonId;
                        var remove = keep == identities[i].GlobalPersonId
                            ? identities[j].GlobalPersonId
                            : identities[i].GlobalPersonId;

                        toMerge.Add((keep, remove));
                    }
                }
            }

            foreach (var (keep, remove) in toMerge.Distinct())
            {
                if (_identities.TryRemove(remove, out var removed))
                {
                    camIds.Remove(remove);
                    if (_identities.TryGetValue(keep, out var keeper))
                    {
                        keeper.MatchCount += removed.MatchCount;
                    }
                    _logger.LogWarning("🔗 Consolidated: {Remove} → {Keep}",
                        remove.ToString()[..8], keep.ToString()[..8]);
                }
            }
        }

        private void UpdateMetadataOnly(Guid personId, int cameraId, BoundingBox? boundingBox)
        {
            if (!_identities.TryGetValue(personId, out var identity))
                return;

            identity.LastSeen = DateTime.UtcNow;
            identity.LastBoundingBox = boundingBox;
            if (cameraId > 0) identity.CameraId = cameraId;
            identity.MatchCount++;

            // Mark as confirmed after multiple matches
            if (identity.MatchCount >= 3)
            {
                identity.IsConfirmed = true;
            }
        }

        private Guid CreateNewIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox)
        {
            var newId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var identity = new PersonIdentity
            {
                GlobalPersonId = newId,
                DbId = 0,
                OriginalFeatureVector = (float[])vector.Values.Clone(),
                LastSeen = now,
                FirstSeen = now,
                CameraId = cameraId,
                MatchCount = 1,
                LastBoundingBox = boundingBox,
                IsConfirmed = false
            };

            _identities[newId] = identity;

            // Track per camera
            if (!_cameraIdentities.TryGetValue(cameraId, out var camIds))
            {
                camIds = new HashSet<Guid>();
                _cameraIdentities[cameraId] = camIds;
            }
            camIds.Add(newId);

            _logger.LogWarning("🆕 NEW: {Id} (camera {Cam}, total: {Total}, cam-total: {CamTotal})",
                newId.ToString()[..8], cameraId, _identities.Count, camIds.Count);

            return newId;
        }

        public bool TryMatch(FeatureVector vector, out Guid personId, out float similarity)
        {
            var activeIdentities = _identities.Values.ToList();
            var result = TryMatchStrict(vector, activeIdentities, 0, null);
            personId = result.PersonId;
            similarity = 1f / (1f + result.BestDistance);
            return result.IsMatch;
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector)
        {
            // Do nothing - vectors are immutable
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector, int cameraId)
        {
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

        /// <summary>
        /// Get confirmed unique person count (more reliable)
        /// </summary>
        public int GetConfirmedIdentityCount()
        {
            return _identities.Values.Count(i => i.IsConfirmed);
        }

        /// <summary>
        /// Get unique count for specific camera
        /// </summary>
        public int GetCameraIdentityCount(int cameraId)
        {
            if (_cameraIdentities.TryGetValue(cameraId, out var ids))
            {
                return ids.Count(id => _identities.TryGetValue(id, out var i) && i.IsConfirmed);
            }
            return 0;
        }

        public void CleanupExpired(TimeSpan expirationTime)
        {
            var threshold = DateTime.UtcNow - expirationTime;
            var expired = _identities
                .Where(kvp => kvp.Value.LastSeen < threshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in expired)
            {
                if (_identities.TryRemove(id, out var identity))
                {
                    if (_cameraIdentities.TryGetValue(identity.CameraId, out var camIds))
                    {
                        camIds.Remove(id);
                    }
                }
            }

            if (expired.Count > 0)
            {
                _logger.LogInformation("🧹 Cleaned up {Count} expired identities", expired.Count);
            }
        }

        public void ClearAllIdentities()
        {
            var count = _identities.Count;
            _identities.Clear();
            _cameraIdentities.Clear();
            _logger.LogWarning("🗑️ Cleared all {Count} identities", count);
        }

        /// <summary>
        /// Clear identities for specific camera only
        /// </summary>
        public void ClearCameraIdentities(int cameraId)
        {
            if (_cameraIdentities.TryRemove(cameraId, out var ids))
            {
                foreach (var id in ids)
                {
                    _identities.TryRemove(id, out _);
                }
                _logger.LogWarning("🗑️ Cleared {Count} identities for camera {Cam}", ids.Count, cameraId);
            }
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