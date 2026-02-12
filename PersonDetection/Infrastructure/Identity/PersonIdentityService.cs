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

        // NEW: Track match stability to prevent ID flipping
        private readonly ConcurrentDictionary<int, MatchStabilityTracker> _trackStability = new();

        // NEW: Track entry detections
        private readonly ConcurrentDictionary<Guid, EntryInfo> _entryTracking = new();

        private readonly IServiceProvider _serviceProvider;
        private readonly IdentitySettings _settings;
        private readonly ILogger<PersonIdentityService> _logger;

        private bool _isInitialized = false;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private DateTime _sessionStartTime = DateTime.UtcNow;
        private readonly ConcurrentDictionary<Guid, DateTime> _sessionPersons = new();

        // Frame dimensions for entry zone calculation
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

            // NEW: Activity tracking
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
                "🚀 PersonIdentityService Configuration:\n" +
                "   ├─ Distance Thresholds: Same={Same:F2}, Global={Global:F2}, MinNew={MinNew:F2}\n" +
                "   ├─ Temporal: Enabled={TempEnabled}, ActiveSec={ActiveSec}, RecentMin={RecentMin}, Penalty={Penalty:F2}\n" +
                "   ├─ Entry Zone: Enabled={EntryEnabled}, Margin={Margin}%, Bonus={Bonus:F2}\n" +
                "   ├─ Stability: Frames={StabFrames}\n" +
                "   ├─ Fast Walker: Enabled={FastEnabled}, MinConf={MinConf:P0}, MinDetect={MinDetect}\n" +
                "   └─ DB Load: Hours={DbHours}",
                _settings.DistanceThreshold,
                _settings.GlobalMatchThreshold,
                _settings.MinDistanceForNewIdentity,
                _settings.EnableTemporalMatching,
                _settings.MaxSecondsForActiveMatch,
                _settings.MaxMinutesForRecentMatch,
                _settings.PenaltyForStaleMatch,
                _settings.EnableEntryZoneDetection,
                _settings.EntryZoneMarginPercent,
                _settings.NewPersonBonusDistance,
                _settings.MatchStabilityFrames,
                _settings.EnableFastWalkerMode,
                _settings.MinConfidenceForConfirmation,
                _settings.MinHighConfidenceDetections,
                _settings.DatabaseLoadHours);
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
                    .OrderByDescending(p => p.LastSeenAt) // Prioritize recently seen
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
        /// Main entry point with full tracking support
        /// </summary>
        public Guid GetOrCreateIdentity(
            FeatureVector vector,
            int cameraId,
            BoundingBox? boundingBox,
            float detectionConfidence,
            int trackId)
        {
            // Validate inputs
            if (boundingBox != null && !IsSufficientSizeForReId(boundingBox))
            {
                if (!(_settings.EnableFastWalkerMode && detectionConfidence >= _settings.MinConfidenceForConfirmation))
                {
                    return Guid.Empty;
                }
            }

            if (!IsValidFeatureVector(vector))
            {
                return Guid.Empty;
            }

            // Check if person is in entry zone (likely NEW person)
            bool isInEntryZone = IsInEntryZone(boundingBox);

            // Try to match
            var matchResult = TryMatchWithTemporalAwareness(vector, cameraId, boundingBox, isInEntryZone);

            // Apply match stability
            var stableMatchId = ApplyMatchStability(trackId, matchResult, cameraId);

            if (stableMatchId != Guid.Empty)
            {
                UpdateIdentityOnMatch(stableMatchId, cameraId, boundingBox, detectionConfidence);
                TrackActiveOnCamera(stableMatchId, cameraId);
                return stableMatchId;
            }

            // Create new identity
            var newId = CreateNewIdentity(vector, cameraId, boundingBox, detectionConfidence, isInEntryZone);
            TrackActiveOnCamera(newId, cameraId);

            return newId;
        }

        #endregion

        #region Core Matching Logic

        private (bool IsMatch, Guid PersonId, float BestDistance, bool IsAmbiguous, bool IsStale)
            TryMatchWithTemporalAwareness(
                FeatureVector vector,
                int cameraId,
                BoundingBox? boundingBox,
                bool isInEntryZone)
        {
            if (_globalIdentities.IsEmpty)
            {
                return (false, Guid.Empty, float.MaxValue, false, false);
            }

            var now = DateTime.UtcNow;

            // Calculate distances with temporal penalties
            var matches = _globalIdentities.Values
                .Select(identity =>
                {
                    var storedVector = new FeatureVector(identity.FeatureVector);
                    var rawDistance = vector.EuclideanDistance(storedVector);

                    // Calculate temporal penalty
                    float temporalPenalty = 0f;
                    bool isStale = false;

                    if (_settings.EnableTemporalMatching)
                    {
                        var timeSinceLastSeen = now - identity.LastActiveTime;

                        if (timeSinceLastSeen.TotalSeconds > _settings.MaxSecondsForActiveMatch)
                        {
                            if (timeSinceLastSeen.TotalMinutes > _settings.MaxMinutesForRecentMatch)
                            {
                                // Stale - apply full penalty
                                temporalPenalty = _settings.PenaltyForStaleMatch;
                                isStale = true;
                            }
                            else
                            {
                                // Recent but not active - apply partial penalty
                                var minutesPassed = (float)timeSinceLastSeen.TotalMinutes;
                                var penaltyRatio = minutesPassed / _settings.MaxMinutesForRecentMatch;
                                temporalPenalty = _settings.PenaltyForStaleMatch * penaltyRatio * 0.5f;
                            }
                        }
                    }

                    var adjustedDistance = rawDistance + temporalPenalty;

                    return new
                    {
                        Identity = identity,
                        RawDistance = rawDistance,
                        AdjustedDistance = adjustedDistance,
                        IsStale = isStale,
                        TimeSinceLastSeen = now - identity.LastActiveTime
                    };
                })
                .OrderBy(m => m.AdjustedDistance)
                .Take(5)
                .ToList();

            if (matches.Count == 0)
                return (false, Guid.Empty, float.MaxValue, false, false);

            var best = matches[0];
            var secondBest = matches.Count > 1 ? matches[1] : null;

            // Determine threshold
            bool isCrossCamera = cameraId > 0 && best.Identity.LastCameraId != cameraId;
            float threshold = isCrossCamera ? _settings.GlobalMatchThreshold : _settings.DistanceThreshold;

            // If person is in entry zone, make it harder to match (they're probably NEW)
            if (isInEntryZone && _settings.EnableEntryZoneDetection)
            {
                threshold -= _settings.NewPersonBonusDistance;
                _logger.LogDebug("🚪 Entry zone detected - threshold reduced to {Thresh:F3}", threshold);
            }

            // Log match attempt
            _logger.LogInformation(
                "{Status} Match: {Id} raw={Raw:F3} adj={Adj:F3} (thresh={Thresh:F3}, stale={Stale}, entry={Entry})",
                best.AdjustedDistance <= threshold ? "✅" : "❌",
                best.Identity.GlobalPersonId.ToString()[..8],
                best.RawDistance,
                best.AdjustedDistance,
                threshold,
                best.IsStale,
                isInEntryZone);

            // Must be below threshold
            if (best.AdjustedDistance > threshold)
            {
                return (false, Guid.Empty, best.RawDistance, false, best.IsStale);
            }

            // Check for required recent activity
            if (_settings.RequireRecentActivityForMatch && best.IsStale)
            {
                _logger.LogInformation(
                    "⏰ Rejecting stale match to {Id} (last seen {Min:F1} min ago)",
                    best.Identity.GlobalPersonId.ToString()[..8],
                    best.TimeSinceLastSeen.TotalMinutes);
                return (false, Guid.Empty, best.RawDistance, false, true);
            }

            // Check ambiguity
            bool isAmbiguous = false;
            if (secondBest != null && secondBest.AdjustedDistance < threshold)
            {
                var ratio = secondBest.AdjustedDistance / Math.Max(best.AdjustedDistance, 0.001f);
                if (ratio < _settings.MinSeparationRatio)
                {
                    isAmbiguous = true;

                    // Still accept if raw distance is very small
                    if (best.RawDistance > _settings.MinDistanceForNewIdentity)
                    {
                        _logger.LogInformation(
                            "⚠️ Ambiguous match rejected: {Id} vs {Id2}",
                            best.Identity.GlobalPersonId.ToString()[..8],
                            secondBest.Identity.GlobalPersonId.ToString()[..8]);
                        return (false, Guid.Empty, best.RawDistance, true, best.IsStale);
                    }
                }
            }

            // Force new identity if in entry zone and match is not very strong
            if (isInEntryZone && best.RawDistance > _settings.MinDistanceForNewIdentity * 1.5f)
            {
                _logger.LogInformation(
                    "🚪 Entry zone: Creating new identity instead of weak match to {Id}",
                    best.Identity.GlobalPersonId.ToString()[..8]);
                return (false, Guid.Empty, best.RawDistance, false, false);
            }

            return (true, best.Identity.GlobalPersonId, best.RawDistance, isAmbiguous, best.IsStale);
        }

        /// <summary>
        /// Apply match stability to prevent ID flipping
        /// </summary>
        private Guid ApplyMatchStability(int trackId,
            (bool IsMatch, Guid PersonId, float BestDistance, bool IsAmbiguous, bool IsStale) matchResult,
            int cameraId)
        {
            if (trackId <= 0 || _settings.MatchStabilityFrames <= 1)
            {
                return matchResult.IsMatch ? matchResult.PersonId : Guid.Empty;
            }

            var key = trackId + (cameraId * 10000); // Unique per camera

            if (!_trackStability.TryGetValue(key, out var tracker))
            {
                tracker = new MatchStabilityTracker
                {
                    CurrentMatchId = matchResult.PersonId,
                    PendingMatchId = Guid.Empty,
                    PendingMatchCount = 0,
                    LastUpdate = DateTime.UtcNow
                };
                _trackStability[key] = tracker;
                return matchResult.IsMatch ? matchResult.PersonId : Guid.Empty;
            }

            // Reset if too much time passed
            if ((DateTime.UtcNow - tracker.LastUpdate).TotalSeconds > 2)
            {
                tracker.CurrentMatchId = matchResult.PersonId;
                tracker.PendingMatchId = Guid.Empty;
                tracker.PendingMatchCount = 0;
            }

            tracker.LastUpdate = DateTime.UtcNow;

            var newMatchId = matchResult.IsMatch ? matchResult.PersonId : Guid.Empty;

            // If same as current, return it
            if (newMatchId == tracker.CurrentMatchId)
            {
                tracker.PendingMatchId = Guid.Empty;
                tracker.PendingMatchCount = 0;
                return tracker.CurrentMatchId;
            }

            // Different match - start counting
            if (newMatchId == tracker.PendingMatchId)
            {
                tracker.PendingMatchCount++;

                if (tracker.PendingMatchCount >= _settings.MatchStabilityFrames)
                {
                    _logger.LogInformation(
                        "🔄 Stable ID change: {Old} → {New} after {Frames} frames",
                        tracker.CurrentMatchId.ToString()[..8],
                        newMatchId == Guid.Empty ? "NEW" : newMatchId.ToString()[..8],
                        tracker.PendingMatchCount);

                    tracker.CurrentMatchId = newMatchId;
                    tracker.PendingMatchId = Guid.Empty;
                    tracker.PendingMatchCount = 0;
                    return newMatchId;
                }
            }
            else
            {
                tracker.PendingMatchId = newMatchId;
                tracker.PendingMatchCount = 1;
            }

            // Return current stable match
            return tracker.CurrentMatchId;
        }

        #endregion

        #region Identity Management

        private Guid CreateNewIdentity(
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

            // Track entry info
            if (isFromEntryZone)
            {
                _entryTracking[newId] = new EntryInfo
                {
                    EntryTime = now,
                    IsFromEntryZone = true,
                    EntryCameraId = cameraId
                };
            }

            // Auto-confirm high confidence new detections in fast walker mode
            if (_settings.EnableFastWalkerMode && confidence >= 0.70f)
            {
                _confirmedPersons[newId] = true;
                identity.IsConfirmedByConfidence = true;
                _logger.LogWarning(
                    "🆕⚡ NEW CONFIRMED (high-conf): {Id} on cam{Cam} conf={Conf:P0}",
                    newId.ToString()[..8], cameraId, confidence);
            }
            else
            {
                _logger.LogInformation(
                    "🆕 NEW: {Id} on cam{Cam} (entry={Entry}, total={Total})",
                    newId.ToString()[..8], cameraId, isFromEntryZone, _globalIdentities.Count);
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

            // Confidence tracking
            if (confidence > 0)
            {
                identity.MaxConfidence = Math.Max(identity.MaxConfidence, confidence);
                identity.ConfidenceHistory.Add(new ConfidenceRecord
                {
                    Timestamp = now,
                    Confidence = confidence,
                    CameraId = cameraId
                });

                // Cleanup old records
                var windowStart = now.AddSeconds(-_settings.FastWalkerTimeWindowSeconds);
                identity.ConfidenceHistory.RemoveAll(r => r.Timestamp < windowStart);

                if (confidence >= _settings.MinConfidenceForConfirmation)
                {
                    identity.HighConfidenceCount++;
                }
            }

            // Confirmation logic
            bool shouldConfirm = false;

            // Method 1: Match count
            if (identity.MatchCount >= _settings.ConfirmationMatchCount)
            {
                shouldConfirm = true;
            }

            // Method 2: High confidence detections
            if (_settings.EnableConfidenceBasedConfirmation)
            {
                var recentHighConf = identity.ConfidenceHistory
                    .Count(r => r.Confidence >= _settings.MinConfidenceForConfirmation);

                if (recentHighConf >= _settings.MinHighConfidenceDetections)
                {
                    shouldConfirm = true;
                    identity.IsConfirmedByConfidence = true;
                }
            }

            // Method 3: Fast walker single high confidence
            if (_settings.EnableFastWalkerMode && confidence >= 0.75f && identity.MatchCount >= 2)
            {
                shouldConfirm = true;
            }

            if (shouldConfirm && !_confirmedPersons.ContainsKey(personId))
            {
                _confirmedPersons[personId] = true;
                _logger.LogInformation("✅ CONFIRMED: {Id} (matches={M}, conf={C:P0})",
                    personId.ToString()[..8], identity.MatchCount, confidence);
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

            // Check if center is near any edge
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
            return cropArea >= (minArea * 0.6) ||
                   (box.Width >= _settings.MinCropWidth && box.Height >= _settings.MinCropHeight);
        }

        private bool IsValidFeatureVector(FeatureVector vector)
        {
            var values = vector.Values;
            if (values == null || values.Length != 512)
                return false;
            if (values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                return false;
            var variance = values.Select(v => v * v).Average() - Math.Pow(values.Average(), 2);
            return variance >= 0.00001f;
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
            var result = TryMatchWithTemporalAwareness(vector, 0, null, false);
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
                _confirmedPersons.ContainsKey(i.GlobalPersonId) &&
                (i.IsConfirmedByConfidence || i.SeenOnCameras.Count > 1 || i.MatchCount >= 3));
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

            // Only cleanup unconfirmed, non-database entries
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

            // Cleanup stability trackers
            var staleTrackers = _trackStability
                .Where(kvp => (DateTime.UtcNow - kvp.Value.LastUpdate).TotalSeconds > 30)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in staleTrackers)
            {
                _trackStability.TryRemove(key, out _);
            }

            // Cleanup active tracking
            foreach (var (_, activeDict) in _cameraActivePersons)
            {
                var expired = activeDict.Where(kvp => kvp.Value < threshold).Select(kvp => kvp.Key).ToList();
                foreach (var id in expired) activeDict.TryRemove(id, out _);
            }

            if (toRemove.Count > 0)
            {
                _logger.LogInformation("🧹 Cleaned {Count} unconfirmed identities", toRemove.Count);
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
                _logger.LogWarning("🗑️ Cleared camera {Cam} tracking ({Count} entries)", cameraId, removed.Count);
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

        // Today's count with caching
        private int _cachedTodayCount = 0;
        private DateTime _lastTodayCountUpdate = DateTime.MinValue;

        public int GetTodayUniqueCount()
        {
            if ((DateTime.UtcNow - _lastTodayCountUpdate).TotalSeconds > 10)
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