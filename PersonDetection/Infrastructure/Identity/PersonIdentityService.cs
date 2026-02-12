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
        // GLOBAL identity store - shared across ALL cameras
        private readonly ConcurrentDictionary<Guid, PersonIdentity> _globalIdentities = new();

        // Per-camera active tracking (for spatial matching)
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, DateTime>> _cameraActivePersons = new();

        // Confirmed unique persons (for accurate counting)
        private readonly ConcurrentDictionary<Guid, bool> _confirmedPersons = new();

        private readonly IServiceProvider _serviceProvider;
        private readonly IdentitySettings _settings;
        private readonly ILogger<PersonIdentityService> _logger;

        private bool _isInitialized = false;
        private readonly SemaphoreSlim _initLock = new(1, 1);


        private DateTime _sessionStartTime = DateTime.UtcNow;
        private readonly ConcurrentDictionary<Guid, DateTime> _sessionPersons = new();


        private class PersonIdentity
        {
            public Guid GlobalPersonId { get; set; }
            public int DbId { get; set; }
            public float[] FeatureVector { get; set; } = null!;
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public int FirstCameraId { get; set; }
            public int LastCameraId { get; set; }
            public int MatchCount { get; set; } = 1;
            public HashSet<int> SeenOnCameras { get; set; } = new();
            public BoundingBox? LastBoundingBox { get; set; }
            public bool IsFromDatabase { get; set; } = false;
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
                "🚀 PersonIdentityService: GlobalMatch={Global}, DistThresh={Dist}, MinNew={MinNew}, LoadDB={LoadDB}",
                _settings.EnableGlobalMatching,
                _settings.DistanceThreshold,
                _settings.MinDistanceForNewIdentity,
                _settings.LoadFromDatabaseOnStartup);

            // Start async initialization
            if (_settings.LoadFromDatabaseOnStartup)
            {
                Task.Run(InitializeFromDatabaseAsync);
            }
            else
            {
                _isInitialized = true;
            }
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
                UpdateVectorOnMatch = false,
                EnableGlobalMatching = true,
                LoadFromDatabaseOnStartup = true
            }, logger)
        {
        }

        /// <summary>
        /// Load ALL known persons from database for global matching
        /// </summary>
        private async Task InitializeFromDatabaseAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                if (_isInitialized) return;

                _logger.LogInformation("📥 Loading global identities from database...");

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                var cutoff = DateTime.UtcNow.AddHours(-_settings.DatabaseLoadHours);

                var persons = await context.UniquePersons
                    .Where(p => p.IsActive && p.LastSeenAt >= cutoff)
                    .OrderByDescending(p => p.TotalSightings)
                    .Take(_settings.MaxIdentitiesInMemory)
                    .ToListAsync();

                int loaded = 0;
                foreach (var person in persons)
                {
                    var features = person.GetFeatureArray();
                    if (features != null && features.Length == 512)
                    {
                        var identity = new PersonIdentity
                        {
                            GlobalPersonId = person.GlobalPersonId,
                            DbId = person.Id,
                            FeatureVector = features,
                            FirstSeen = person.FirstSeenAt,
                            LastSeen = person.LastSeenAt,
                            FirstCameraId = person.FirstSeenCameraId,
                            LastCameraId = person.LastSeenCameraId,
                            MatchCount = person.TotalSightings,
                            IsFromDatabase = true
                        };

                        identity.SeenOnCameras.Add(person.FirstSeenCameraId);
                        identity.SeenOnCameras.Add(person.LastSeenCameraId);

                        _globalIdentities[person.GlobalPersonId] = identity;

                        // Mark as confirmed if has multiple sightings
                        if (person.TotalSightings >= _settings.ConfirmationMatchCount)
                        {
                            _confirmedPersons[person.GlobalPersonId] = true;
                        }

                        loaded++;
                    }
                }

                _isInitialized = true;
                _logger.LogWarning("✅ Loaded {Count} global identities from database ({Confirmed} confirmed)",
                    loaded, _confirmedPersons.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load identities from database");
                _isInitialized = true; // Continue anyway
            }
            finally
            {
                _initLock.Release();
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
            // Check crop size
            if (boundingBox != null && !IsSufficientSizeForReId(boundingBox))
            {
                _logger.LogDebug("📏 Crop too small ({W}x{H}) for ReID",
                    boundingBox.Width, boundingBox.Height);
                return Guid.Empty;
            }

            // Validate feature vector
            if (!IsValidFeatureVector(vector))
            {
                _logger.LogWarning("⚠️ Invalid feature vector");
                return Guid.Empty;
            }

            // GLOBAL matching
            var matchResult = TryMatchGlobal(vector, cameraId, boundingBox);

            if (matchResult.IsMatch)
            {
                UpdateIdentityOnMatch(matchResult.PersonId, cameraId, boundingBox, matchResult.IsAmbiguous);
                TrackActiveOnCamera(matchResult.PersonId, cameraId);
                return matchResult.PersonId;
            }

            // Check minimum distance - force match if very close
            if (matchResult.BestDistance < _settings.MinDistanceForNewIdentity &&
                matchResult.BestMatchId != Guid.Empty)
            {
                _logger.LogWarning("🔗 Distance {Dist:F4} < MinNew {Min:F4} - forcing match to {Id}",
                    matchResult.BestDistance, _settings.MinDistanceForNewIdentity,
                    matchResult.BestMatchId.ToString()[..8]);

                UpdateIdentityOnMatch(matchResult.BestMatchId, cameraId, boundingBox, true);
                TrackActiveOnCamera(matchResult.BestMatchId, cameraId);
                return matchResult.BestMatchId;
            }

            // Create new GLOBAL identity
            var newId = CreateNewGlobalIdentity(vector, cameraId, boundingBox);
            TrackActiveOnCamera(newId, cameraId);

            return newId;
        }

        private bool IsSufficientSizeForReId(BoundingBox box)
        {
            // Use area-based check instead of strict width/height
            var minArea = _settings.MinCropWidth * _settings.MinCropHeight;
            var cropArea = box.Width * box.Height;

            // Also allow if one dimension is good even if other is small
            var hasGoodWidth = box.Width >= _settings.MinCropWidth;
            var hasGoodHeight = box.Height >= _settings.MinCropHeight;

            return cropArea >= minArea || (hasGoodWidth && hasGoodHeight);
        }

        private bool IsValidFeatureVector(FeatureVector vector)
        {
            var values = vector.Values;

            if (values == null || values.Length != 512)
                return false;

            if (values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                return false;

            var variance = values.Select(v => v * v).Average() - Math.Pow(values.Average(), 2);
            if (variance < 0.0001f)
            {
                _logger.LogWarning("⚠️ Low variance feature vector");
                return false;
            }

            return true;
        }

        private void TrackActiveOnCamera(Guid personId, int cameraId)
        {
            if (cameraId <= 0) return;

            if (!_cameraActivePersons.TryGetValue(cameraId, out var activeDict))
            {
                activeDict = new ConcurrentDictionary<Guid, DateTime>();
                _cameraActivePersons[cameraId] = activeDict;
            }

            activeDict[personId] = DateTime.UtcNow;
        }

        /// <summary>
        /// GLOBAL matching - checks against ALL known identities across ALL cameras
        /// </summary>
        private (bool IsMatch, Guid PersonId, float BestDistance, Guid BestMatchId, bool IsAmbiguous) TryMatchGlobal(
       FeatureVector vector,
       int cameraId,
       BoundingBox? boundingBox)
        {
            if (_globalIdentities.IsEmpty)
            {
                _logger.LogDebug("🔍 No global identities to match against");
                return (false, Guid.Empty, float.MaxValue, Guid.Empty, false);
            }

            // Calculate distances to ALL global identities
            var distances = _globalIdentities.Values
                .Select(identity =>
                {
                    var storedVector = new FeatureVector(identity.FeatureVector);
                    var distance = vector.EuclideanDistance(storedVector);
                    return (identity.GlobalPersonId, distance, identity);
                })
                .OrderBy(d => d.distance)
                .Take(10)
                .ToList();

            if (distances.Count == 0)
                return (false, Guid.Empty, float.MaxValue, Guid.Empty, false);

            var best = distances[0];
            var secondBest = distances.Count > 1
                ? distances[1]
                : (GlobalPersonId: Guid.Empty, distance: float.MaxValue, identity: (PersonIdentity?)null);

            // Determine threshold
            var threshold = _settings.DistanceThreshold;
            var isCrossCamera = best.identity.LastCameraId != cameraId;
            if (_settings.EnableGlobalMatching && isCrossCamera)
            {
                threshold = _settings.GlobalMatchThreshold;
            }

            _logger.LogInformation(
                "🌍 GLOBAL: Best={Id}:{Dist:F4} (cam{Cam}), Second={SecondDist:F4}, Thresh={Thresh:F4}, Cross={Cross}",
                best.GlobalPersonId.ToString()[..8],
                best.distance,
                best.identity.LastCameraId,
                secondBest.distance,
                threshold,
                isCrossCamera);

            // Rule 1: Must be below threshold
            if (best.distance > threshold)
            {
                _logger.LogInformation("❌ NO MATCH: {Dist:F4} > threshold {Thresh:F4}",
                    best.distance, threshold);
                return (false, Guid.Empty, best.distance, best.GlobalPersonId, false);
            }

            // Rule 2: Check separation
            bool isAmbiguous = false;
            if (_settings.RequireMinimumSeparation &&
                secondBest.GlobalPersonId != Guid.Empty &&
                secondBest.distance < threshold)
            {
                var ratio = secondBest.distance / Math.Max(best.distance, 0.001f);

                if (ratio < _settings.MinSeparationRatio)
                {
                    isAmbiguous = true;

                    // Only accept if distance is very small AND below MinDistanceForNewIdentity
                    if (best.distance < _settings.MinDistanceForNewIdentity)
                    {
                        _logger.LogWarning(
                            "⚠️ AMBIGUOUS MATCH (accepted): Best={Best:F4}, Second={Second:F4}, Ratio={Ratio:F2}",
                            best.distance, secondBest.distance, ratio);
                        return (true, best.GlobalPersonId, best.distance, best.GlobalPersonId, true);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "❌ AMBIGUOUS MATCH (rejected): Best={Best:F4}, Second={Second:F4}, Ratio={Ratio:F2}",
                            best.distance, secondBest.distance, ratio);
                        return (false, Guid.Empty, best.distance, best.GlobalPersonId, true);
                    }
                }
            }

            // Clear match
            var matchType = isCrossCamera ? "CROSS-CAMERA" : "SAME-CAMERA";
            _logger.LogInformation("✅ {Type} MATCH: {Id} (dist={Dist:F4}, seen on {Cams} cameras)",
                matchType,
                best.GlobalPersonId.ToString()[..8],
                best.distance,
                best.identity.SeenOnCameras.Count);

            return (true, best.GlobalPersonId, best.distance, best.GlobalPersonId, false);
        }

        private void UpdateIdentityOnMatch(Guid personId, int cameraId, BoundingBox? boundingBox, bool isAmbiguous = false)
        {
            if (!_globalIdentities.TryGetValue(personId, out var identity))
                return;

            identity.LastSeen = DateTime.UtcNow;
            identity.LastBoundingBox = boundingBox;
            identity.MatchCount++;

            if (cameraId > 0)
            {
                identity.LastCameraId = cameraId;
                identity.SeenOnCameras.Add(cameraId);
            }

            // Only confirm if NOT ambiguous OR if settings allow it
            if (!isAmbiguous || _settings.AmbiguousMatchAsConfirmed)
            {
                if (identity.MatchCount >= _settings.ConfirmationMatchCount)
                {
                    _confirmedPersons[personId] = true;
                }
            }
        }

        private Guid CreateNewGlobalIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox)
        {
            var newId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var identity = new PersonIdentity
            {
                GlobalPersonId = newId,
                DbId = 0,
                FeatureVector = (float[])vector.Values.Clone(),
                FirstSeen = now,
                LastSeen = now,
                FirstCameraId = cameraId,
                LastCameraId = cameraId,
                MatchCount = 1,
                LastBoundingBox = boundingBox,
                IsFromDatabase = false
            };

            if (cameraId > 0)
            {
                identity.SeenOnCameras.Add(cameraId);
            }

            _globalIdentities[newId] = identity;

            _logger.LogWarning("🆕 NEW GLOBAL IDENTITY: {Id} on camera {Cam} (total: {Total})",
                newId.ToString()[..8], cameraId, _globalIdentities.Count);

            return newId;
        }

        /// <summary>
        /// Called when identity is saved to database - update our record
        /// </summary>
        public void OnIdentitySavedToDatabase(Guid personId, int dbId)
        {
            if (_globalIdentities.TryGetValue(personId, out var identity))
            {
                identity.DbId = dbId;
                identity.IsFromDatabase = true;
            }
        }

        public bool TryMatch(FeatureVector vector, out Guid personId, out float similarity)
        {
            var result = TryMatchGlobal(vector, 0, null);
            personId = result.PersonId;
            similarity = 1f / (1f + result.BestDistance);
            return result.IsMatch;
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector)
        {
            // No-op - we don't update feature vectors
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector, int cameraId)
        {
            UpdateIdentityOnMatch(personId, cameraId, null);
        }

        public void SetDbId(Guid personId, int dbId)
        {
            OnIdentitySavedToDatabase(personId, dbId);
        }

        public int GetDbId(Guid personId)
        {
            return _globalIdentities.TryGetValue(personId, out var identity) ? identity.DbId : 0;
        }

        /// <summary>
        /// Total identities in memory
        /// </summary>
        public int GetActiveIdentityCount() => _globalIdentities.Count;

        /// <summary>
        /// Confirmed unique persons (more reliable count)
        /// </summary>
        public int GetConfirmedIdentityCount() => _confirmedPersons.Count;

        /// <summary>
        /// Get unique count for specific camera (persons seen on this camera)
        /// </summary>
        public int GetCameraIdentityCount(int cameraId)
        {
            return _globalIdentities.Values
                .Count(i => i.SeenOnCameras.Contains(cameraId) &&
                           _confirmedPersons.ContainsKey(i.GlobalPersonId));
        }

        /// <summary>
        /// Get total unique persons across ALL cameras (global count)
        /// </summary>
        public int GetGlobalUniqueCount()
        {
            return _confirmedPersons.Count;
        }

        /// <summary>
        /// Get counts with quality breakdown
        /// </summary>
        public (int Total, int Confirmed, int HighConfidence) GetDetailedCounts()
        {
            var total = _globalIdentities.Count;
            var confirmed = _confirmedPersons.Count;

            // High confidence = confirmed AND seen on multiple cameras OR has many matches
            var highConfidence = _globalIdentities.Values
                .Count(i => _confirmedPersons.ContainsKey(i.GlobalPersonId) &&
                           (i.SeenOnCameras.Count > 1 || i.MatchCount >= 5));

            return (total, confirmed, highConfidence);
        }

        /// <summary>
        /// Get persons currently active on a specific camera
        /// </summary>
        public int GetCurrentlyActiveCount(int cameraId)
        {
            if (!_cameraActivePersons.TryGetValue(cameraId, out var activeDict))
                return 0;

            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            return activeDict.Count(kvp => kvp.Value >= cutoff);
        }

        public void CleanupExpired(TimeSpan expirationTime)
        {
            var threshold = DateTime.UtcNow - expirationTime;

            // Only cleanup in-memory identities that are NOT from database
            var toRemove = _globalIdentities
                .Where(kvp => !kvp.Value.IsFromDatabase && kvp.Value.LastSeen < threshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in toRemove)
            {
                _globalIdentities.TryRemove(id, out _);
                _confirmedPersons.TryRemove(id, out _);
            }

            // Cleanup active tracking
            foreach (var (camId, activeDict) in _cameraActivePersons)
            {
                var expiredActive = activeDict
                    .Where(kvp => kvp.Value < threshold)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var id in expiredActive)
                {
                    activeDict.TryRemove(id, out _);
                }
            }

            if (toRemove.Count > 0)
            {
                _logger.LogInformation("🧹 Cleaned up {Count} expired in-memory identities", toRemove.Count);
            }
        }

        public void ClearAllIdentities()
        {
            var count = _globalIdentities.Count;
            _globalIdentities.Clear();
            _confirmedPersons.Clear();
            _cameraActivePersons.Clear();
            _logger.LogWarning("🗑️ Cleared all {Count} identities", count);
        }

        public void ClearCameraIdentities(int cameraId)
        {
            // Don't actually remove global identities, just clear active tracking
            if (_cameraActivePersons.TryRemove(cameraId, out var removed))
            {
                _logger.LogWarning("🗑️ Cleared active tracking for camera {Cam} ({Count} entries)",
                    cameraId, removed.Count);
            }
        }

        /// <summary>
        /// Reload identities from database (useful after bulk import)
        /// </summary>
        public async Task ReloadFromDatabaseAsync()
        {
            _isInitialized = false;
            await InitializeFromDatabaseAsync();
        }

        /// <summary>
        /// Get statistics for debugging
        /// </summary>
        public Dictionary<string, object> GetStatistics()
        {
            return new Dictionary<string, object>
            {
                ["TotalIdentities"] = _globalIdentities.Count,
                ["ConfirmedIdentities"] = _confirmedPersons.Count,
                ["FromDatabase"] = _globalIdentities.Values.Count(i => i.IsFromDatabase),
                ["ActiveCameras"] = _cameraActivePersons.Count,
                ["IsInitialized"] = _isInitialized
            };
        }

        public int GetSessionUniqueCount()
        {
            return _sessionPersons.Count(kvp => kvp.Value >= _sessionStartTime);
        }

        // Add method to get today's count
        /// <summary>
        /// Get unique persons seen TODAY (since midnight) across ALL cameras
        /// </summary>
        public async Task<int> GetTodayUniqueCountAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                var todayStart = DateTime.UtcNow.Date;

                var count = await context.UniquePersons
                    .Where(p => p.IsActive &&
                               (p.FirstSeenAt >= todayStart || p.LastSeenAt >= todayStart))
                    .CountAsync();

                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get today's unique count from database");

                // Fallback to in-memory count for today
                var todayStart = DateTime.UtcNow.Date;
                return _globalIdentities.Values
                    .Count(i => i.FirstSeen >= todayStart || i.LastSeen >= todayStart);
            }
        }

        /// <summary>
        /// Get unique persons seen TODAY - synchronous version with caching
        /// </summary>
        private int _cachedTodayCount = 0;
        private DateTime _lastTodayCountUpdate = DateTime.MinValue;
        private readonly TimeSpan _todayCountCacheTime = TimeSpan.FromSeconds(10);

        public int GetTodayUniqueCount()
        {
            // Cache the count to avoid hitting DB too often
            if ((DateTime.UtcNow - _lastTodayCountUpdate) > _todayCountCacheTime)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                    var todayStart = DateTime.UtcNow.Date;

                    _cachedTodayCount = context.UniquePersons
                        .Where(p => p.IsActive &&
                                   (p.FirstSeenAt >= todayStart || p.LastSeenAt >= todayStart))
                        .Count();

                    _lastTodayCountUpdate = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get today's count, using in-memory");

                    var todayStart = DateTime.UtcNow.Date;
                    _cachedTodayCount = _globalIdentities.Values
                        .Count(i => i.FirstSeen >= todayStart || i.LastSeen >= todayStart);
                    _lastTodayCountUpdate = DateTime.UtcNow;
                }
            }

            return _cachedTodayCount;
        }


        // Call this when starting a new session
        public void StartNewSession()
        {
            _sessionStartTime = DateTime.UtcNow;
            _sessionPersons.Clear();
            _logger.LogWarning("🔄 New session started at {Time}", _sessionStartTime);
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