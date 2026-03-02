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
    using Microsoft.EntityFrameworkCore;
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
            public bool IsCurrentlyActive => (DateTime.UtcNow - LastActiveTime).TotalSeconds < 60;
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

        private enum MatchType
        {
            NoMatch,
            Ambiguous,
            DefiniteMatch
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

            if (_settings.LoadFromDatabaseOnStartup)
            {
                Task.Run(InitializeFromDatabaseAsync);
            }
            else
            {
                _isInitialized = true;
            }
        }

        private void LogSettings()
        {
            _logger.LogWarning(
                "🚀 PersonIdentityService Settings:\n" +
                "   ├─ Thresholds: DefiniteMatch={Definite:F2}, Ambiguous={Ambiguous:F2}\n" +
                "   ├─ Zone: [0-{Definite:F2}]=MATCH, [{Definite:F2}-{Ambiguous:F2}]=AMBIGUOUS→NEW, [>{Ambiguous:F2}]=NEW\n" +
                "   ├─ Temporal: MaxActive={Active}s, MaxRecent={Recent}min, Penalty={Penalty:F2}\n" +
                "   ├─ Entry Zone: Enabled={EntryEnabled}, Margin={Margin}%, Bonus={Bonus:F2}\n" +
                "   ├─ Confirmation: InstantConf={Instant:P0}, MinConf={MinConf:P0}, VeryHigh={VeryHigh:P0}\n" +
                "   └─ FastWalker: Enabled={FastEnabled}, Window={Window}s",
                _settings.MinDistanceForNewIdentity,
                _settings.DistanceThreshold,
                _settings.MinDistanceForNewIdentity,
                _settings.MinDistanceForNewIdentity,
                _settings.DistanceThreshold,
                _settings.DistanceThreshold,
                _settings.MaxSecondsForActiveMatch,
                _settings.MaxMinutesForRecentMatch,
                _settings.PenaltyForStaleMatch,
                _settings.EnableEntryZoneDetection,
                _settings.EntryZoneMarginPercent,
                _settings.NewPersonBonusDistance,
                _settings.InstantConfirmConfidence,
                _settings.MinConfidenceForConfirmation,
                _settings.VeryHighConfidenceThreshold,
                _settings.EnableFastWalkerMode,
                _settings.FastWalkerTimeWindowSeconds);
        }

        public PersonIdentityService(
            IServiceProvider serviceProvider,
            float distanceThreshold,
            bool updateVectorOnMatch,
            ILogger<PersonIdentityService> logger)
            : this(serviceProvider, new IdentitySettings
            {
                DistanceThreshold = distanceThreshold,
                UpdateVectorOnMatch = updateVectorOnMatch,
                EnableGlobalMatching = true,
                LoadFromDatabaseOnStartup = true
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
                    if (features != null && features.Length == _settings.FeatureVectorDimension)
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
        /// Main entry point - HIGH CONFIDENCE = INSTANT NEW
        /// </summary>
        public Guid GetOrCreateIdentity(
            FeatureVector vector,
            int cameraId,
            BoundingBox? boundingBox,
            float detectionConfidence,
            int trackId)
        {
            // Size check
            if (boundingBox != null && !IsSufficientSizeForReId(boundingBox))
            {
                if (detectionConfidence < _settings.MinConfidenceForSmallDetection)
                {
                    return Guid.Empty;
                }
            }

            if (!IsValidFeatureVector(vector))
            {
                return Guid.Empty;
            }

            bool isInEntryZone = IsInEntryZone(boundingBox);

            // ═══════════════════════════════════════════════════════════════
            // PRIORITY 1: HIGH CONFIDENCE (≥60%) = INSTANT NEW UNIQUE
            // Only match if VERY close (distance < 0.10)
            // ═══════════════════════════════════════════════════════════════
            if (detectionConfidence >= _settings.VeryHighConfidenceThreshold)
            {
                var matchResult = TryMatchWithAmbiguousZone(vector, cameraId, boundingBox, isInEntryZone, detectionConfidence);

                // Only reuse if EXTREMELY close match (< 0.10)
                if (matchResult.MatchType == MatchType.DefiniteMatch &&
                    matchResult.BestDistance < _settings.MinDistanceForNewIdentity)
                {
                    UpdateIdentityOnMatch(matchResult.PersonId, cameraId, boundingBox, detectionConfidence);
                    TrackActiveOnCamera(matchResult.PersonId, cameraId);

                    _logger.LogInformation(
                        "✅ HIGH-CONF MATCH: conf={Conf:P0} dist={Dist:F3} → {Id}",
                        detectionConfidence, matchResult.BestDistance, matchResult.PersonId.ToString()[..8]);

                    return matchResult.PersonId;
                }
                else
                {
                    // HIGH CONFIDENCE but not exact match → CREATE NEW
                    _logger.LogInformation(
                        "🆕 HIGH-CONF NEW: conf={Conf:P0} dist={Dist:F3} (threshold={Thresh:F2})",
                        detectionConfidence, matchResult.BestDistance, _settings.MinDistanceForNewIdentity);

                    var newId = CreateNewIdentity(vector, cameraId, boundingBox, detectionConfidence, isInEntryZone, "HIGH-CONF-NEW");
                    TrackActiveOnCamera(newId, cameraId);
                    return newId;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // PRIORITY 2: MEDIUM-HIGH CONFIDENCE (≥55%) = Likely new
            // Only match if close (distance < MinDistanceForNewIdentity)
            // ═══════════════════════════════════════════════════════════════
            if (detectionConfidence >= _settings.InstantConfirmConfidence)
            {
                var matchResult = TryMatchWithAmbiguousZone(vector, cameraId, boundingBox, isInEntryZone, detectionConfidence);

                if (matchResult.MatchType == MatchType.DefiniteMatch)
                {
                    UpdateIdentityOnMatch(matchResult.PersonId, cameraId, boundingBox, detectionConfidence);
                    TrackActiveOnCamera(matchResult.PersonId, cameraId);

                    _logger.LogInformation(
                        "✅ MED-CONF MATCH: conf={Conf:P0} dist={Dist:F3} → {Id}",
                        detectionConfidence, matchResult.BestDistance, matchResult.PersonId.ToString()[..8]);

                    return matchResult.PersonId;
                }
                else
                {
                    // MEDIUM CONFIDENCE + not definite match → CREATE NEW
                    _logger.LogInformation(
                        "🆕 MED-CONF NEW: conf={Conf:P0} dist={Dist:F3}",
                        detectionConfidence, matchResult.BestDistance);

                    var newId = CreateNewIdentity(vector, cameraId, boundingBox, detectionConfidence, isInEntryZone, "MED-CONF-NEW");
                    TrackActiveOnCamera(newId, cameraId);
                    return newId;
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // PRIORITY 3: LOWER CONFIDENCE - Normal matching logic
            // ═══════════════════════════════════════════════════════════════
            var normalMatchResult = TryMatchWithAmbiguousZone(vector, cameraId, boundingBox, isInEntryZone, detectionConfidence);

            if (normalMatchResult.MatchType == MatchType.DefiniteMatch)
            {
                UpdateIdentityOnMatch(normalMatchResult.PersonId, cameraId, boundingBox, detectionConfidence);
                TrackActiveOnCamera(normalMatchResult.PersonId, cameraId);

                _logger.LogDebug(
                    "✅ DEFINITE MATCH: conf={Conf:P0} dist={Dist:F3} → {Id}",
                    detectionConfidence, normalMatchResult.BestDistance, normalMatchResult.PersonId.ToString()[..8]);

                return normalMatchResult.PersonId;
            }
            else if (normalMatchResult.MatchType == MatchType.Ambiguous)
            {
                _logger.LogInformation(
                    "🔶 AMBIGUOUS → NEW: conf={Conf:P0} dist={Dist:F3}",
                    detectionConfidence, normalMatchResult.BestDistance);

                var newId = CreateNewIdentity(vector, cameraId, boundingBox, detectionConfidence, isInEntryZone, "AMBIGUOUS");
                TrackActiveOnCamera(newId, cameraId);
                return newId;
            }
            else
            {
                _logger.LogInformation(
                    "🆕 NO MATCH → NEW: conf={Conf:P0} dist={Dist:F3}",
                    detectionConfidence, normalMatchResult.BestDistance);

                var newId = CreateNewIdentity(vector, cameraId, boundingBox, detectionConfidence, isInEntryZone, "NO-MATCH");
                TrackActiveOnCamera(newId, cameraId);
                return newId;
            }
        }

        #endregion

        #region Matching Logic

        private (MatchType MatchType, Guid PersonId, float BestDistance)
     TryMatchWithAmbiguousZone(
         FeatureVector vector,
         int cameraId,
         BoundingBox? boundingBox,
         bool isInEntryZone,
         float detectionConfidence)
        {
            if (_globalIdentities.IsEmpty)
            {
                _logger.LogDebug("🆕 No identities in memory - will create new");
                return (MatchType.NoMatch, Guid.Empty, float.MaxValue);
            }

            var now = DateTime.UtcNow;

            var activeIdentities = _globalIdentities.Values
                .Where(i => (now - i.LastActiveTime).TotalMinutes <= _settings.MaxMinutesForRecentMatch)
                .ToList();

            if (activeIdentities.Count == 0)
            {
                _logger.LogDebug("🆕 No active identities to match against");
                return (MatchType.NoMatch, Guid.Empty, float.MaxValue);
            }

            var matches = activeIdentities
                .Select(identity =>
                {
                    var storedVector = new FeatureVector(identity.FeatureVector);
                    var rawDistance = vector.EuclideanDistance(storedVector);

                    float temporalPenalty = 0f;

                    if (_settings.EnableTemporalMatching)
                    {
                        var timeSinceLastSeen = now - identity.LastActiveTime;

                        if (timeSinceLastSeen.TotalSeconds > _settings.MaxSecondsForActiveMatch)
                        {
                            var minutesPassed = (float)timeSinceLastSeen.TotalMinutes;
                            var penaltyRatio = Math.Min(1f, minutesPassed / _settings.MaxMinutesForRecentMatch);
                            temporalPenalty = _settings.PenaltyForStaleMatch * penaltyRatio;
                        }
                    }

                    var adjustedDistance = rawDistance + temporalPenalty;

                    return new
                    {
                        Identity = identity,
                        RawDistance = rawDistance,
                        AdjustedDistance = adjustedDistance
                    };
                })
                .OrderBy(m => m.AdjustedDistance)
                .ToList();

            if (matches.Count == 0)
                return (MatchType.NoMatch, Guid.Empty, float.MaxValue);

            var best = matches[0];

            float definiteMatchThreshold = _settings.MinDistanceForNewIdentity;
            float noMatchThreshold = _settings.DistanceThreshold;

            if (isInEntryZone && _settings.EnableEntryZoneDetection)
            {
                definiteMatchThreshold -= _settings.NewPersonBonusDistance * 0.5f;
                noMatchThreshold -= _settings.NewPersonBonusDistance;
            }

            MatchType matchType;

            if (best.AdjustedDistance <= definiteMatchThreshold)
            {
                matchType = MatchType.DefiniteMatch;

                // ═══════════════════════════════════════════════════════════
                // LOG when person is matched to existing (this is why 60% might be "missed")
                // ═══════════════════════════════════════════════════════════
                _logger.LogInformation(
                    "✅ DEFINITE MATCH: conf={Conf:P0} dist={Dist:F3} → existing {Id}",
                    detectionConfidence, best.AdjustedDistance, best.Identity.GlobalPersonId.ToString()[..8]);
            }
            else if (best.AdjustedDistance <= noMatchThreshold)
            {
                matchType = MatchType.Ambiguous;
            }
            else
            {
                matchType = MatchType.NoMatch;
            }

            _logger.LogDebug(
                "🔍 Match: conf={Conf:P0} dist={Dist:F3} (raw={Raw:F3}) type={Type} thresholds=[{Def:F2},{NoM:F2}] entry={Entry}",
                detectionConfidence, best.AdjustedDistance, best.RawDistance, matchType,
                definiteMatchThreshold, noMatchThreshold, isInEntryZone);

            return (matchType, best.Identity.GlobalPersonId, best.AdjustedDistance);
        }

        #endregion

        #region Identity Creation

        private Guid CreateNewIdentity(
     FeatureVector vector,
     int cameraId,
     BoundingBox? boundingBox,
     float confidence,
     bool isFromEntryZone,
     string reason)
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

            // ═══════════════════════════════════════════════════════════
            // INSTANT CONFIRM if confidence is good enough
            // ═══════════════════════════════════════════════════════════
            bool shouldConfirm = false;
            string confirmType = "";

            // Very high confidence (≥60%) - instant confirm
            if (confidence >= _settings.VeryHighConfidenceThreshold)
            {
                shouldConfirm = true;
                confirmType = "VERY-HIGH";
            }
            // Instant confirm threshold (≥55%)
            else if (confidence >= _settings.InstantConfirmConfidence)
            {
                shouldConfirm = true;
                confirmType = "INSTANT";
            }
            // Min confidence threshold
            else if (confidence >= _settings.MinConfidenceForConfirmation)
            {
                shouldConfirm = true;
                confirmType = "MIN-CONF";
            }

            if (shouldConfirm)
            {
                _confirmedPersons[newId] = true;
                identity.IsConfirmedByConfidence = true;
                _logger.LogInformation(
                    "🆕✅ {Reason} [{ConfirmType}]: {Id} cam={Cam} conf={Conf:P0} (total={Total})",
                    reason, confirmType, newId.ToString()[..8], cameraId, confidence, _confirmedPersons.Count);
            }
            else
            {
                _logger.LogDebug(
                    "🆕 {Reason} (pending): {Id} cam={Cam} conf={Conf:P0}",
                    reason, newId.ToString()[..8], cameraId, confidence);
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

            if (!_confirmedPersons.ContainsKey(personId))
            {
                _confirmedPersons[personId] = true;
                _logger.LogInformation("✅ CONFIRMED on match: {Id}", personId.ToString()[..8]);
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
            return cropArea >= (minArea * _settings.MinCropAreaMultiplier) ||
                   (box.Width >= _settings.MinCropWidth * _settings.MinCropDimensionMultiplier &&
                    box.Height >= _settings.MinCropHeight * _settings.MinCropDimensionMultiplier);
        }

        private bool IsValidFeatureVector(FeatureVector vector)
        {
            var values = vector.Values;
            if (values == null || values.Length != _settings.FeatureVectorDimension)
                return false;
            if (values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                return false;
            var variance = values.Select(v => v * v).Average() - Math.Pow(values.Average(), 2);
            return variance >= _settings.MinFeatureVariance;
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
            var result = TryMatchWithAmbiguousZone(vector, 0, null, false, 0.5f);
            personId = result.PersonId;
            similarity = 1f / (1f + result.BestDistance);
            return result.MatchType == MatchType.DefiniteMatch;
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
                _confirmedPersons.ContainsKey(i.GlobalPersonId) &&
                (i.IsConfirmedByConfidence || i.SeenOnCameras.Count > 1 || i.MatchCount >= 2));
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
                .Where(kvp => !kvp.Value.IsFromDatabase &&
                             !_confirmedPersons.ContainsKey(kvp.Key) &&
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

            if (toRemove.Count > 0)
            {
                _logger.LogDebug("🧹 Cleaned {Count} unconfirmed identities", toRemove.Count);
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
            ["CurrentlyActive"] = _globalIdentities.Values.Count(i => i.IsCurrentlyActive),
            ["ActiveCameras"] = _cameraActivePersons.Count,
            ["IsInitialized"] = _isInitialized
        };

        // ✅ NEW — non-blocking async refresh
        private int _cachedTodayCount = 0;
        private DateTime _lastTodayCountUpdate = DateTime.MinValue;
        private volatile bool _isRefreshingTodayCount = false;

        public int GetTodayUniqueCount()
        {
            // Return cached value immediately (never blocks)
            // Trigger async refresh if cache is stale
            if ((DateTime.UtcNow - _lastTodayCountUpdate).TotalSeconds > 10
                && !_isRefreshingTodayCount)
            {
                _isRefreshingTodayCount = true;
                _ = RefreshTodayCountAsync();
            }
            return _cachedTodayCount;
        }

        private async Task RefreshTodayCountAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();
                var todayStart = DateTime.UtcNow.Date;

                _cachedTodayCount = await context.UniquePersons
                    .AsNoTracking()
                    .Where(p => p.IsActive &&
                               (p.FirstSeenAt >= todayStart || p.LastSeenAt >= todayStart))
                    .CountAsync();

                _lastTodayCountUpdate = DateTime.UtcNow;
            }
            catch
            {
                // Fallback: use in-memory identities
                var todayStart = DateTime.UtcNow.Date;
                _cachedTodayCount = _globalIdentities.Values
                    .Count(i => i.FirstSeen >= todayStart || i.LastSeen >= todayStart);
                _lastTodayCountUpdate = DateTime.UtcNow;
            }
            finally
            {
                _isRefreshingTodayCount = false;
            }
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