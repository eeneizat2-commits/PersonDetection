// PersonDetection.Infrastructure/Identity/PersonIdentityService.cs
namespace PersonDetection.Infrastructure.Identity
{
    using System.Collections.Concurrent;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Domain.Services;
    using PersonDetection.Domain.ValueObjects;
    using PersonDetection.Infrastructure.Context;

    public class PersonIdentityService : IPersonIdentityMatcher
    {
        private readonly ConcurrentDictionary<Guid, PersonIdentity> _globalIdentities = new();
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, DateTime>> _cameraActivePersons = new();
        private readonly ConcurrentDictionary<Guid, bool> _confirmedPersons = new();
        private readonly ConcurrentDictionary<int, MatchStabilityTracker> _trackStability = new();
        private readonly ConcurrentDictionary<Guid, EntryInfo> _entryTracking = new();

        private readonly IServiceProvider _serviceProvider;
        private readonly IdentitySettings _settings;
        private readonly ILogger<PersonIdentityService> _logger;

        private bool _isInitialized = false;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private DateTime _sessionStartTime = DateTime.UtcNow;
        private readonly ConcurrentDictionary<Guid, DateTime> _sessionPersons = new();

        private int _frameWidth = 1280;
        private int _frameHeight = 720;

        #region Inner Classes

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
            public List<ConfidenceRecord> ConfidenceHistory { get; set; } = new();
            public float MaxConfidence { get; set; } = 0f;
            public int HighConfidenceCount { get; set; } = 0;
            public bool IsConfirmedByConfidence { get; set; } = false;
            public DateTime LastActiveTime { get; set; }

            public bool IsCurrentlyActive(int timeoutSeconds) =>
                (DateTime.UtcNow - LastActiveTime).TotalSeconds < timeoutSeconds;
        }

        private class ConfidenceRecord
        {
            public DateTime Timestamp { get; set; }
            public float Confidence { get; set; }
            public int CameraId { get; set; }
        }

        private class MatchStabilityTracker
        {
            public Guid CurrentMatchId { get; set; }
            public Guid PendingMatchId { get; set; }
            public int PendingMatchCount { get; set; }
            public DateTime LastUpdate { get; set; }
        }

        private class EntryInfo
        {
            public DateTime EntryTime { get; set; }
            public bool IsFromEntryZone { get; set; }
            public int EntryCameraId { get; set; }
        }

        #endregion

        public PersonIdentityService(
            IServiceProvider serviceProvider,
            IdentitySettings settings,
            ILogger<PersonIdentityService> logger)
        {
            _serviceProvider = serviceProvider;
            _settings = settings;
            _logger = logger;

            LogSettings();

            // Only load from DB if explicitly enabled AND hours > 0
            if (_settings.LoadFromDatabaseOnStartup && _settings.DatabaseLoadHours > 0)
            {
                Task.Run(InitializeFromDatabaseAsync);
            }
            else
            {
                _isInitialized = true;
                _logger.LogWarning("🚀 Starting FRESH - no database identities loaded (OnlyMatchActive={Active})",
                    _settings.OnlyMatchActiveIdentities);
            }
        }

        private void LogSettings()
        {
            _logger.LogWarning(
                "🚀 AGGRESSIVE PersonIdentityService:\n" +
                "   ├─ Thresholds: Same={Same:F2}, MinNew={MinNew:F2}\n" +
                "   ├─ OnlyMatchActive: {OnlyActive}, Timeout={Timeout}s\n" +
                "   ├─ TreatAmbiguousAsNew: {Ambiguous}\n" +
                "   ├─ LoadFromDB: {LoadDB}, Hours={Hours}\n" +
                "   ├─ InstantConfirm: {Instant:P0}\n" +
                "   └─ EntryZone: {Entry}%, Bonus={Bonus:F2}",
                _settings.DistanceThreshold,
                _settings.MinDistanceForNewIdentity,
                _settings.OnlyMatchActiveIdentities,
                _settings.ActiveIdentityTimeoutSeconds,
                _settings.TreatAmbiguousAsNew,
                _settings.LoadFromDatabaseOnStartup,
                _settings.DatabaseLoadHours,
                _settings.InstantConfirmConfidence,
                _settings.EntryZoneMarginPercent,
                _settings.NewPersonBonusDistance);
        }

        public PersonIdentityService(
            IServiceProvider serviceProvider,
            float distanceThreshold,
            bool updateVectorOnMatch,
            ILogger<PersonIdentityService> logger)
            : this(serviceProvider, new IdentitySettings
            {
                DistanceThreshold = distanceThreshold,
                UpdateVectorOnMatch = false,
                EnableGlobalMatching = false,
                LoadFromDatabaseOnStartup = false
            }, logger)
        {
        }

        #region Initialization

        private async Task InitializeFromDatabaseAsync()
        {
            await _initLock.WaitAsync();
            try
            {
                if (_isInitialized) return;

                if (!_settings.LoadFromDatabaseOnStartup || _settings.DatabaseLoadHours <= 0)
                {
                    _isInitialized = true;
                    _logger.LogWarning("📥 Database loading DISABLED - starting fresh");
                    return;
                }

                _logger.LogInformation("📥 Loading identities from last {Hours} hours...", _settings.DatabaseLoadHours);

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                var cutoff = DateTime.UtcNow.AddHours(-_settings.DatabaseLoadHours);

                var persons = await context.UniquePersons
                    .Where(p => p.IsActive && p.LastSeenAt >= cutoff)
                    .OrderByDescending(p => p.LastSeenAt)
                    .ThenByDescending(p => p.TotalSightings)
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
                            LastActiveTime = person.LastSeenAt,
                            FirstCameraId = person.FirstSeenCameraId,
                            LastCameraId = person.LastSeenCameraId,
                            MatchCount = person.TotalSightings,
                            IsFromDatabase = true,
                            IsConfirmedByConfidence = true
                        };

                        identity.SeenOnCameras.Add(person.FirstSeenCameraId);
                        identity.SeenOnCameras.Add(person.LastSeenCameraId);

                        _globalIdentities[person.GlobalPersonId] = identity;
                        _confirmedPersons[person.GlobalPersonId] = true;
                        loaded++;
                    }
                }

                _isInitialized = true;
                _logger.LogWarning("✅ Loaded {Count} identities from database", loaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to load from database");
                _isInitialized = true;
            }
            finally
            {
                _initLock.Release();
            }
        }

        #endregion

        #region Public Methods

        public void SetFrameDimensions(int width, int height)
        {
            _frameWidth = width;
            _frameHeight = height;
        }

        public Guid GetOrCreateIdentity(FeatureVector vector)
            => GetOrCreateIdentity(vector, 0, null, 0f, 0);

        public Guid GetOrCreateIdentity(FeatureVector vector, int cameraId)
            => GetOrCreateIdentity(vector, cameraId, null, 0f, 0);

        public Guid GetOrCreateIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox)
            => GetOrCreateIdentity(vector, cameraId, boundingBox, 0f, 0);

        public Guid GetOrCreateIdentity(FeatureVector vector, int cameraId, BoundingBox? boundingBox, float detectionConfidence)
            => GetOrCreateIdentity(vector, cameraId, boundingBox, detectionConfidence, 0);

        /// <summary>
        /// AGGRESSIVE version - prioritizes creating NEW identities
        /// Ambiguous matches = NEW identity
        /// </summary>
        public Guid GetOrCreateIdentity(
            FeatureVector vector,
            int cameraId,
            BoundingBox? boundingBox,
            float detectionConfidence,
            int trackId)
        {
            // Very relaxed size check
            if (boundingBox != null && !IsSufficientSizeForReId(boundingBox))
            {
                if (detectionConfidence < 0.20f)
                {
                    return Guid.Empty;
                }
            }

            if (!IsValidFeatureVector(vector))
            {
                return Guid.Empty;
            }

            bool isInEntryZone = IsInEntryZone(boundingBox);

            // ★★★ KEY: Only match against ACTIVE identities, treat ambiguous as NEW ★★★
            var matchResult = TryMatchAggressive(vector, cameraId, boundingBox, isInEntryZone, detectionConfidence);

            // If ambiguous, CREATE NEW
            if (matchResult.IsAmbiguous && _settings.TreatAmbiguousAsNew)
            {
                _logger.LogInformation("⚠️ AMBIGUOUS → Creating NEW identity");
                var newId = CreateNewIdentityAggressive(vector, cameraId, boundingBox, detectionConfidence, isInEntryZone);
                TrackActiveOnCamera(newId, cameraId);
                return newId;
            }

            if (matchResult.IsMatch && matchResult.PersonId != Guid.Empty)
            {
                UpdateIdentityOnMatch(matchResult.PersonId, cameraId, boundingBox, detectionConfidence);
                TrackActiveOnCamera(matchResult.PersonId, cameraId);
                return matchResult.PersonId;
            }

            // CREATE NEW - this is the default!
            var newIdentity = CreateNewIdentityAggressive(vector, cameraId, boundingBox, detectionConfidence, isInEntryZone);
            TrackActiveOnCamera(newIdentity, cameraId);

            return newIdentity;
        }

        #endregion

        #region AGGRESSIVE Matching - Favors NEW identities

        private (bool IsMatch, Guid PersonId, float BestDistance, bool IsAmbiguous)
            TryMatchAggressive(
                FeatureVector vector,
                int cameraId,
                BoundingBox? boundingBox,
                bool isInEntryZone,
                float detectionConfidence)
        {
            var now = DateTime.UtcNow;
            var activeTimeout = _settings.ActiveIdentityTimeoutSeconds > 0
                ? _settings.ActiveIdentityTimeoutSeconds
                : 20;

            // ★★★ ONLY get CURRENTLY ACTIVE identities ★★★
            List<PersonIdentity> candidateIdentities;

            if (_settings.OnlyMatchActiveIdentities)
            {
                // STRICT: Only match identities seen in last N seconds
                candidateIdentities = _globalIdentities.Values
                    .Where(i => !i.IsFromDatabase && i.IsCurrentlyActive(activeTimeout))
                    .ToList();

                if (candidateIdentities.Count == 0)
                {
                    _logger.LogDebug("🆕 No active non-DB identities ({Total} total) - creating new",
                        _globalIdentities.Count);
                    return (false, Guid.Empty, float.MaxValue, false);
                }
            }
            else
            {
                // Fallback: all identities with time filter
                candidateIdentities = _globalIdentities.Values
                    .Where(i => (now - i.LastActiveTime).TotalMinutes <= _settings.MaxMinutesForRecentMatch)
                    .ToList();

                if (candidateIdentities.Count == 0)
                {
                    return (false, Guid.Empty, float.MaxValue, false);
                }
            }

            // Calculate distances
            var matches = candidateIdentities
                .Select(identity =>
                {
                    var storedVector = new FeatureVector(identity.FeatureVector);
                    var rawDistance = vector.EuclideanDistance(storedVector);

                    // Time-based penalty
                    float timePenalty = 0f;
                    var timeSinceLastSeen = now - identity.LastActiveTime;

                    if (timeSinceLastSeen.TotalSeconds > 5)
                    {
                        timePenalty = (float)(timeSinceLastSeen.TotalSeconds - 5) * 0.015f;
                        timePenalty = Math.Min(timePenalty, _settings.PenaltyForStaleMatch);
                    }

                    // Database entries get HEAVY penalty (effectively excluded)
                    if (identity.IsFromDatabase)
                    {
                        timePenalty += 0.50f;
                    }

                    var adjustedDistance = rawDistance + timePenalty;

                    return new
                    {
                        Identity = identity,
                        RawDistance = rawDistance,
                        AdjustedDistance = adjustedDistance,
                        TimeSinceLastSeen = timeSinceLastSeen
                    };
                })
                .OrderBy(m => m.AdjustedDistance)
                .Take(3)
                .ToList();

            if (matches.Count == 0)
                return (false, Guid.Empty, float.MaxValue, false);

            var best = matches[0];
            var secondBest = matches.Count > 1 ? matches[1] : null;

            // ★★★ STRICT threshold ★★★
            float threshold = _settings.DistanceThreshold;

            // Entry zone: Even stricter (more new identities)
            if (isInEntryZone)
            {
                threshold -= _settings.NewPersonBonusDistance;
            }

            // High confidence: Stricter (distinct person likely)
            if (detectionConfidence >= 0.50f)
            {
                threshold -= 0.05f;
            }

            // ★★★ CHECK FOR AMBIGUITY ★★★
            bool isAmbiguous = false;
            if (secondBest != null && _settings.TreatAmbiguousAsNew)
            {
                // If two matches are close in distance, it's ambiguous
                var distanceDiff = secondBest.AdjustedDistance - best.AdjustedDistance;
                var ratio = secondBest.AdjustedDistance / Math.Max(best.AdjustedDistance, 0.001f);

                if (distanceDiff < 0.10f || ratio < _settings.MinSeparationRatio)
                {
                    isAmbiguous = true;
                    _logger.LogDebug("⚠️ AMBIGUOUS: best={Best:F3}, second={Second:F3}, diff={Diff:F3}",
                        best.AdjustedDistance, secondBest.AdjustedDistance, distanceDiff);
                }
            }

            // Log decision
            var decision = best.AdjustedDistance <= threshold ? "MATCH" : "NEW";
            if (isAmbiguous) decision = "AMBIGUOUS→NEW";

            _logger.LogDebug(
                "🔍 {Decision}: raw={Raw:F3} adj={Adj:F3} thresh={Thresh:F3} (active={Active})",
                decision, best.RawDistance, best.AdjustedDistance, threshold, candidateIdentities.Count);

            // If ambiguous, return as ambiguous (will create new)
            if (isAmbiguous)
            {
                return (false, Guid.Empty, best.RawDistance, true);
            }

            // Must be BELOW threshold to match
            if (best.AdjustedDistance > threshold)
            {
                return (false, Guid.Empty, best.RawDistance, false);
            }

            // Extra check: reject if time since last seen > N seconds and distance not very close
            if (best.TimeSinceLastSeen.TotalSeconds > 10 && best.RawDistance > 0.12f)
            {
                _logger.LogDebug("⏰ Rejecting stale match (>10s, dist={Dist:F3})", best.RawDistance);
                return (false, Guid.Empty, best.RawDistance, false);
            }

            return (true, best.Identity.GlobalPersonId, best.RawDistance, false);
        }

        #endregion

        #region AGGRESSIVE Identity Creation

        private Guid CreateNewIdentityAggressive(
            FeatureVector vector,
            int cameraId,
            BoundingBox? boundingBox,
            float confidence,
            bool isFromEntryZone)
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
                LastActiveTime = now,
                FirstCameraId = cameraId,
                LastCameraId = cameraId,
                MatchCount = 1,
                LastBoundingBox = boundingBox,
                IsFromDatabase = false,
                MaxConfidence = confidence
            };

            if (confidence > 0)
            {
                identity.ConfidenceHistory.Add(new ConfidenceRecord
                {
                    Timestamp = now,
                    Confidence = confidence,
                    CameraId = cameraId
                });

                if (confidence >= _settings.MinConfidenceForConfirmation)
                {
                    identity.HighConfidenceCount = 1;
                }
            }

            if (cameraId > 0)
            {
                identity.SeenOnCameras.Add(cameraId);
            }

            _globalIdentities[newId] = identity;

            if (isFromEntryZone)
            {
                _entryTracking[newId] = new EntryInfo
                {
                    EntryTime = now,
                    IsFromEntryZone = true,
                    EntryCameraId = cameraId
                };
            }

            // ★★★ AGGRESSIVE: INSTANT CONFIRMATION for ANY reasonable confidence ★★★
            float instantThreshold = _settings.InstantConfirmConfidence > 0
                ? _settings.InstantConfirmConfidence
                : 0.30f;

            // Confirm if confidence meets instant threshold
            if (confidence >= instantThreshold)
            {
                _confirmedPersons[newId] = true;
                identity.IsConfirmedByConfidence = true;
                _logger.LogInformation(
                    "🆕✅ NEW UNIQUE: {Id} cam={Cam} conf={Conf:P0} (total={Total})",
                    newId.ToString()[..8], cameraId, confidence, _confirmedPersons.Count);
            }
            // Also confirm if confidence is decent
            else if (confidence >= 0.20f)
            {
                _confirmedPersons[newId] = true;
                _logger.LogInformation(
                    "🆕✅ NEW (low-conf): {Id} cam={Cam} conf={Conf:P0}",
                    newId.ToString()[..8], cameraId, confidence);
            }
            // Even very low confidence gets confirmed
            else if (confidence > 0)
            {
                _confirmedPersons[newId] = true;
                _logger.LogDebug(
                    "🆕✅ NEW (very-low): {Id} conf={Conf:P0}",
                    newId.ToString()[..8], confidence);
            }

            return newId;
        }

        private void UpdateIdentityOnMatch(
            Guid personId,
            int cameraId,
            BoundingBox? boundingBox,
            float confidence)
        {
            if (!_globalIdentities.TryGetValue(personId, out var identity))
                return;

            var now = DateTime.UtcNow;
            identity.LastSeen = now;
            identity.LastActiveTime = now;
            identity.LastBoundingBox = boundingBox;
            identity.MatchCount++;

            if (cameraId > 0)
            {
                identity.LastCameraId = cameraId;
                identity.SeenOnCameras.Add(cameraId);
            }

            if (confidence > 0)
            {
                identity.MaxConfidence = Math.Max(identity.MaxConfidence, confidence);
                identity.ConfidenceHistory.Add(new ConfidenceRecord
                {
                    Timestamp = now,
                    Confidence = confidence,
                    CameraId = cameraId
                });

                var windowStart = now.AddSeconds(-_settings.FastWalkerTimeWindowSeconds);
                identity.ConfidenceHistory.RemoveAll(r => r.Timestamp < windowStart);

                if (confidence >= _settings.MinConfidenceForConfirmation)
                {
                    identity.HighConfidenceCount++;
                }
            }

            // Confirm on any match
            if (!_confirmedPersons.ContainsKey(personId))
            {
                _confirmedPersons[personId] = true;
            }
        }

        #endregion

        #region Helper Methods

        private bool IsInEntryZone(BoundingBox? box)
        {
            if (box == null || !_settings.EnableEntryZoneDetection)
                return false;

            var marginX = _frameWidth * _settings.EntryZoneMarginPercent / 100f;
            var marginY = _frameHeight * _settings.EntryZoneMarginPercent / 100f;

            var centerX = box.X + box.Width / 2f;
            var centerY = box.Y + box.Height / 2f;

            bool nearLeft = centerX < marginX;
            bool nearRight = centerX > (_frameWidth - marginX);
            bool nearTop = centerY < marginY;
            bool nearBottom = centerY > (_frameHeight - marginY);

            return nearLeft || nearRight || nearTop || nearBottom;
        }

        private bool IsSufficientSizeForReId(BoundingBox box)
        {
            var minArea = _settings.MinCropWidth * _settings.MinCropHeight;
            var cropArea = box.Width * box.Height;
            return cropArea >= (minArea * 0.25) ||
                   (box.Width >= _settings.MinCropWidth * 0.5 && box.Height >= _settings.MinCropHeight * 0.5);
        }

        private bool IsValidFeatureVector(FeatureVector vector)
        {
            var values = vector.Values;
            if (values == null || values.Length != 512)
                return false;
            if (values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                return false;
            var variance = values.Select(v => v * v).Average() - Math.Pow(values.Average(), 2);
            return variance >= 0.0000001f;
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
            _sessionPersons[personId] = DateTime.UtcNow;
        }

        #endregion

        #region Public Query Methods

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
            var result = TryMatchAggressive(vector, 0, null, false, 0.5f);
            personId = result.PersonId;
            similarity = 1f / (1f + result.BestDistance);
            return result.IsMatch;
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector) { }

        public void UpdateIdentity(Guid personId, FeatureVector vector, int cameraId)
        {
            UpdateIdentityOnMatch(personId, cameraId, null, 0f);
        }

        public void SetDbId(Guid personId, int dbId) => OnIdentitySavedToDatabase(personId, dbId);

        public int GetDbId(Guid personId) =>
            _globalIdentities.TryGetValue(personId, out var identity) ? identity.DbId : 0;

        public int GetActiveIdentityCount() => _globalIdentities.Count;
        public int GetConfirmedIdentityCount() => _confirmedPersons.Count;

        public int GetCameraIdentityCount(int cameraId) =>
            _globalIdentities.Values.Count(i =>
                i.SeenOnCameras.Contains(cameraId) &&
                _confirmedPersons.ContainsKey(i.GlobalPersonId));

        public int GetGlobalUniqueCount() => _confirmedPersons.Count;

        public (int Total, int Confirmed, int HighConfidence) GetDetailedCounts()
        {
            var total = _globalIdentities.Count;
            var confirmed = _confirmedPersons.Count;
            var highConf = _globalIdentities.Values.Count(i =>
                _confirmedPersons.ContainsKey(i.GlobalPersonId) && i.IsConfirmedByConfidence);
            return (total, confirmed, highConf);
        }

        public int GetCurrentlyActiveCount(int cameraId)
        {
            if (!_cameraActivePersons.TryGetValue(cameraId, out var activeDict))
                return 0;
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            return activeDict.Count(kvp => kvp.Value >= cutoff);
        }

        public int GetSessionUniqueCount() =>
            _sessionPersons.Count(kvp => kvp.Value >= _sessionStartTime);

        public void CleanupExpired(TimeSpan expirationTime)
        {
            var threshold = DateTime.UtcNow - expirationTime;

            var toRemove = _globalIdentities
                .Where(kvp => !_confirmedPersons.ContainsKey(kvp.Key) &&
                             kvp.Value.LastSeen < threshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in toRemove)
            {
                _globalIdentities.TryRemove(id, out _);
                _entryTracking.TryRemove(id, out _);
            }

            var staleTrackers = _trackStability
                .Where(kvp => (DateTime.UtcNow - kvp.Value.LastUpdate).TotalSeconds > 10)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in staleTrackers)
            {
                _trackStability.TryRemove(key, out _);
            }

            foreach (var (_, activeDict) in _cameraActivePersons)
            {
                var expired = activeDict.Where(kvp => kvp.Value < threshold).Select(kvp => kvp.Key).ToList();
                foreach (var id in expired) activeDict.TryRemove(id, out _);
            }
        }

        public void ClearAllIdentities()
        {
            _globalIdentities.Clear();
            _confirmedPersons.Clear();
            _cameraActivePersons.Clear();
            _sessionPersons.Clear();
            _trackStability.Clear();
            _entryTracking.Clear();
            _logger.LogWarning("🗑️ Cleared all identities");
        }

        public void ClearCameraIdentities(int cameraId)
        {
            if (_cameraActivePersons.TryRemove(cameraId, out var removed))
            {
                _logger.LogWarning("🗑️ Cleared camera {Cam} ({Count} entries)", cameraId, removed.Count);
            }
        }

        public async Task ReloadFromDatabaseAsync()
        {
            _isInitialized = false;
            await InitializeFromDatabaseAsync();
        }

        public Dictionary<string, object> GetStatistics() => new()
        {
            ["TotalIdentities"] = _globalIdentities.Count,
            ["ConfirmedIdentities"] = _confirmedPersons.Count,
            ["ConfidenceConfirmed"] = _globalIdentities.Values.Count(i => i.IsConfirmedByConfidence),
            ["FromDatabase"] = _globalIdentities.Values.Count(i => i.IsFromDatabase),
            ["CurrentlyActive"] = _globalIdentities.Values.Count(i => i.IsCurrentlyActive(_settings.ActiveIdentityTimeoutSeconds)),
            ["ActiveCameras"] = _cameraActivePersons.Count,
            ["IsInitialized"] = _isInitialized
        };

        private int _cachedTodayCount = 0;
        private DateTime _lastTodayCountUpdate = DateTime.MinValue;

        public int GetTodayUniqueCount()
        {
            if ((DateTime.UtcNow - _lastTodayCountUpdate).TotalSeconds > 5)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();
                    var todayStart = DateTime.UtcNow.Date;
                    _cachedTodayCount = context.UniquePersons
                        .Where(p => p.IsActive && (p.FirstSeenAt >= todayStart || p.LastSeenAt >= todayStart))
                        .Count();
                    _lastTodayCountUpdate = DateTime.UtcNow;
                }
                catch
                {
                    var todayStart = DateTime.UtcNow.Date;
                    _cachedTodayCount = _globalIdentities.Values
                        .Count(i => i.FirstSeen >= todayStart || i.LastSeen >= todayStart);
                    _lastTodayCountUpdate = DateTime.UtcNow;
                }
            }
            return _cachedTodayCount;
        }

        public void StartNewSession()
        {
            _sessionStartTime = DateTime.UtcNow;
            _sessionPersons.Clear();
            _trackStability.Clear();
            _logger.LogWarning("🔄 New session started");
        }

        #endregion
    }

    public struct PointF
    {
        public float X { get; set; }
        public float Y { get; set; }
        public PointF(float x, float y) { X = x; Y = y; }
    }
}