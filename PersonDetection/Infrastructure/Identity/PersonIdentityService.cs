// PersonDetection.Infrastructure/Identity/PersonIdentityService.cs
namespace PersonDetection.Infrastructure.Identity
{
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Services;
    using PersonDetection.Domain.ValueObjects;
    using PersonDetection.Infrastructure.Context;
    using PersonDetection.Infrastructure.Services;
    using System.Collections.Concurrent;
    using System.Runtime.CompilerServices;

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
        private readonly ConcurrentDictionary<Guid, bool> _pendingSave = new();

        private int _frameWidth = 1280;
        private int _frameHeight = 720;

        private int _totalConfirmedEver = 0;

        private DateTime _lastFlush = DateTime.MinValue;

        #region Inner Classes

        private class PersonIdentity
        {
            public readonly object SyncLock = new();  // ← ADD THIS LINE
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

                _logger.LogInformation(
                    "📥 Loading identities from last {Hours} hours (max {Max})...",
                    _settings.DatabaseLoadHours,
                    _settings.MaxIdentitiesInMemory);

                // ✅ FIX: Get today's count FIRST, before any EF Core queries
                try
                {
                    using var countConn = new Microsoft.Data.SqlClient.SqlConnection(
                        "Server=DESKTOP-QML0799;Database=DetectionContext;" +
                        "Trusted_Connection=True;TrustServerCertificate=True;" +
                        "Connection Timeout=30;Command Timeout=40;Pooling=false;");
                    await countConn.OpenAsync();

                    using var countCmd = countConn.CreateCommand();
                    countCmd.CommandText = @"
    SELECT COUNT(*)
    FROM UniquePersons WITH (NOLOCK)
    WHERE FirstSeenAt >= CAST(SYSUTCDATETIME() AS DATE)
    OPTION (MAXDOP 1)";
                    countCmd.CommandTimeout = 60;

                    var countResult = await countCmd.ExecuteScalarAsync();
                    var todayCount = Convert.ToInt32(countResult ?? 0);

                    _todayConfirmedCount = todayCount;
                    _cachedTodayCount = todayCount;
                    _startupConfirmedCount = todayCount;
                    _todayCounterDate = DateTime.UtcNow.Date;
                    _lastTodayCountUpdate = DateTime.UtcNow;

                    _logger.LogWarning("📊 Today count from DB: {Today}", todayCount);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("📊 Today count failed, starting from 0: {Err}", ex.Message);
                }

                // Now load identities (existing code below)
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();
                context.Database.SetCommandTimeout(TimeSpan.FromSeconds(180));

                var cutoff = DateTime.UtcNow.AddHours(-_settings.DatabaseLoadHours);
                var batchSize = 200;
                var loaded = 0;
                var skip = 0;
                var maxToLoad = _settings.MaxIdentitiesInMemory;

                // ... rest of batch loading stays exactly the same ...
                while (loaded < maxToLoad)
                {
                    var currentBatchSize = Math.Min(batchSize, maxToLoad - loaded);

                    List<BatchPersonDto> batch;
                    try
                    {
                        // ✅ NEW: Select ONLY needed columns (no ThumbnailData, no Label)
                        // ✅ NEW — LEFT JOIN with UniquePersonFeatures, fallback to old column
                        batch = await (from p in context.UniquePersons
                                       join f in context.UniquePersonFeatures
                                       on p.GlobalPersonId equals f.GlobalPersonId into features
                                       from f in features.DefaultIfEmpty()     // LEFT JOIN
                                       where p.IsActive && p.LastSeenAt >= cutoff
                                       orderby p.LastSeenAt descending, p.TotalSightings descending
                                       select new BatchPersonDto
                                       {
                                           Id = p.Id,
                                           GlobalPersonId = p.GlobalPersonId,
                                           // ✅ New table first, fallback to old column (transition period)
                                           FeatureVector = f != null ? f.FeatureVector : p.FeatureVector,
                                           FirstSeenAt = p.FirstSeenAt,
                                           LastSeenAt = p.LastSeenAt,
                                           FirstSeenCameraId = p.FirstSeenCameraId,
                                           LastSeenCameraId = p.LastSeenCameraId,
                                           TotalSightings = p.TotalSightings
                                       })
                                      .Skip(skip)
                                      .Take(currentBatchSize)
                                      .AsNoTracking()
                                      .ToListAsync();
                    }
                    catch (Exception ex)
                    {
                        // ✅ NEW: Per-batch error handling (doesn't lose already loaded data)
                        _logger.LogWarning(ex,
                            "📥 Batch load failed at skip={Skip}, loaded so far: {Loaded}",
                            skip, loaded);
                        break;
                    }

                    if (batch.Count == 0) break;

                    // 7. ✅ SAME LOOP LOGIC — identical to old code
                    foreach (var person in batch)
                    {
                        var features = ParseFeatureVector(person.FeatureVector);  // ✅ SAME as GetFeatureArray()
                        if (features != null && features.Length == _settings.FeatureVectorDimension)
                        {
                            // 8. ✅ SAME — Create identity (identical fields)
                            var identity = new PersonIdentity
                            {
                                GlobalPersonId = person.GlobalPersonId,       // ✅ SAME
                                DbId = person.Id,                             // ✅ SAME
                                FeatureVector = features,                     // ✅ SAME
                                FirstSeen = person.FirstSeenAt,               // ✅ SAME
                                LastSeen = person.LastSeenAt,                 // ✅ SAME
                                LastActiveTime = person.LastSeenAt,           // ✅ SAME
                                FirstCameraId = person.FirstSeenCameraId,     // ✅ SAME
                                LastCameraId = person.LastSeenCameraId,       // ✅ SAME
                                MatchCount = person.TotalSightings,           // ✅ SAME
                                IsFromDatabase = true,                        // ✅ SAME
                                IsConfirmedByConfidence = true                // ✅ SAME
                            };

                            identity.SeenOnCameras.Add(person.FirstSeenCameraId);  // ✅ SAME
                            identity.SeenOnCameras.Add(person.LastSeenCameraId);   // ✅ SAME

                            // 9. ✅ SAME — Store in dictionaries
                            _globalIdentities[person.GlobalPersonId] = identity;
                            _confirmedPersons[person.GlobalPersonId] = true;
                            loaded++;
                        }
                    }

                    skip += batch.Count;
                    if (batch.Count < currentBatchSize) break;  // ✅ NEW: stop when no more data
                }

                _totalConfirmedEver = _confirmedPersons.Count;
                _isInitialized = true;

                // ✅ REMOVED: No more today count query at the end

                _logger.LogWarning("✅ Loaded {Count} identities from database (scanned {Scanned})",
                    loaded, skip);
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
            var recentCutoff = now.AddMinutes(-_settings.MaxMinutesForRecentMatch);

            // 🆕 FIX: Single pass, no LINQ, no Sort, no Clone
            float bestAdjustedDistance = float.MaxValue;
            float bestRawDistance = float.MaxValue;
            PersonIdentity? bestIdentity = null;
            var queryValues = vector.Values;

            foreach (var identity in _globalIdentities.Values)
            {
                // Quick temporal filter
                if (identity.LastActiveTime < recentCutoff) continue;

                float rawDistance;
                lock (identity.SyncLock)
                {
                    // 🆕 FIX: Compute distance IN-PLACE — no Clone()!
                    rawDistance = ComputeEuclideanDistanceInPlace(queryValues, identity.FeatureVector);
                }

                // 🆕 FIX: Early exit — if raw distance alone can't beat best, skip
                if (rawDistance > bestAdjustedDistance + _settings.PenaltyForStaleMatch)
                    continue;

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

                if (adjustedDistance < bestAdjustedDistance)
                {
                    bestAdjustedDistance = adjustedDistance;
                    bestRawDistance = rawDistance;
                    bestIdentity = identity;
                }
            }

            if (bestIdentity == null)
                return (MatchType.NoMatch, Guid.Empty, float.MaxValue);

            float definiteMatchThreshold = _settings.MinDistanceForNewIdentity;
            float noMatchThreshold = _settings.DistanceThreshold;

            if (isInEntryZone && _settings.EnableEntryZoneDetection)
            {
                definiteMatchThreshold -= _settings.NewPersonBonusDistance * 0.5f;
                noMatchThreshold -= _settings.NewPersonBonusDistance;
            }

            MatchType matchType;
            if (bestAdjustedDistance <= definiteMatchThreshold)
            {
                matchType = MatchType.DefiniteMatch;
                _logger.LogInformation(
                    "✅ DEFINITE MATCH: conf={Conf:P0} dist={Dist:F3} → existing {Id}",
                    detectionConfidence, bestAdjustedDistance, bestIdentity.GlobalPersonId.ToString()[..8]);
            }
            else if (bestAdjustedDistance <= noMatchThreshold)
            {
                matchType = MatchType.Ambiguous;
            }
            else
            {
                matchType = MatchType.NoMatch;
            }

            _logger.LogDebug(
                "🔍 Match: conf={Conf:P0} dist={Dist:F3} (raw={Raw:F3}) type={Type} thresholds=[{Def:F2},{NoM:F2}] entry={Entry}",
                detectionConfidence, bestAdjustedDistance, bestRawDistance, matchType,
                definiteMatchThreshold, noMatchThreshold, isInEntryZone);

            return (matchType, bestIdentity.GlobalPersonId, bestAdjustedDistance);
        }

        // 🆕 NEW METHOD — Add anywhere in the class:
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ComputeEuclideanDistanceInPlace(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return float.MaxValue;

            float sum = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                var diff = a[i] - b[i];
                sum += diff * diff;
            }
            return MathF.Sqrt(sum);
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

            if (confidence >= _settings.VeryHighConfidenceThreshold)
            {
                shouldConfirm = true;
                confirmType = "VERY-HIGH";
            }
            else if (confidence >= _settings.InstantConfirmConfidence)
            {
                shouldConfirm = true;
                confirmType = "INSTANT";
            }
            else if (confidence >= _settings.MinConfidenceForConfirmation)
            {
                shouldConfirm = true;
                confirmType = "MIN-CONF";
            }

            if (shouldConfirm)
            {
                _confirmedPersons[newId] = true;
                identity.IsConfirmedByConfidence = true;
                _pendingSave[newId] = true;
                Interlocked.Increment(ref _totalConfirmedEver);
                Interlocked.Increment(ref _todayConfirmedCount);
                _logger.LogInformation(
                    "🆕✅ {Reason} [{ConfirmType}]: {Id} cam={Cam} conf={Conf:P0} (total={Total})",
                    reason, confirmType, newId.ToString()[..8], cameraId, confidence, _totalConfirmedEver);
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

            lock (identity.SyncLock)
            {
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
                    identity.ConfidenceHistory.RemoveAll(r => r == null || r.Timestamp < windowStart);

                    if (confidence >= _settings.MinConfidenceForConfirmation)
                    {
                        identity.HighConfidenceCount++;
                    }
                }
            }

            if (!_confirmedPersons.ContainsKey(personId))
            {
                _confirmedPersons[personId] = true;
                Interlocked.Increment(ref _totalConfirmedEver); // 🆕 ADD
                Interlocked.Increment(ref _todayConfirmedCount);
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


        /// <summary>
        /// Check if a person identity has been saved to the database
        /// </summary>
        public bool IsIdentitySavedToDb(Guid personId)
        {
            if (_globalIdentities.TryGetValue(personId, out var identity))
            {
                return identity.DbId > 0;
            }
            return false;
        }

        /// <summary>
        /// Get all confirmed persons that haven't been saved to DB yet
        /// These MUST be saved before they can be cleaned up
        /// </summary>
        public List<(Guid GlobalPersonId, float[] Features, int CameraId)> GetUnsavedConfirmedPersons()
        {
            var unsaved = new List<(Guid, float[], int)>();

            foreach (var kvp in _confirmedPersons)
            {
                if (_globalIdentities.TryGetValue(kvp.Key, out var identity))
                {
                    if (identity.DbId == 0 && identity.FeatureVector != null)
                    {
                        lock (identity.SyncLock)
                        {
                            unsaved.Add((
                                identity.GlobalPersonId,
                                (float[])identity.FeatureVector.Clone(),
                                identity.LastCameraId
                            ));
                        }
                    }
                }
            }

            return unsaved;
        }


        public async Task FlushUnsavedToDatabaseAsync(int maxToFlush = 0)
        {
            try
            {
                var unsaved = new List<(Guid GlobalPersonId, float[] Features, int CameraId, DateTime FirstSeen)>();

                foreach (var kvp in _confirmedPersons)
                {
                    if (_globalIdentities.TryGetValue(kvp.Key, out var identity))
                    {
                        lock (identity.SyncLock)
                        {
                            if (identity.DbId == 0 && identity.FeatureVector != null)
                            {
                                unsaved.Add((
                                    identity.GlobalPersonId,
                                    (float[])identity.FeatureVector.Clone(),
                                    identity.LastCameraId,
                                    identity.FirstSeen
                                ));
                            }
                        }
                    }
                }

                if (unsaved.Count == 0) return;

                if (maxToFlush > 0 && unsaved.Count > maxToFlush)
                {
                    unsaved = unsaved.Take(maxToFlush).ToList();
                }

                _logger.LogInformation("💾 Flushing {Count} unsaved persons...", unsaved.Count);

                var connString = "Server=DESKTOP-QML0799;Database=DetectionContext;" +
                    "Trusted_Connection=True;TrustServerCertificate=True;" +
                    "Connection Timeout=30;Command Timeout=120;Pooling=false;";

                int saved = 0;
                int reconciled = 0;
                int failed = 0;

                // ═══════════════════════════════════════════════════════════════
                // PHASE 1: RECONCILE — find persons already saved by SP
                //          SELECT only — no locks, no inserts, fast on HDD
                // ═══════════════════════════════════════════════════════════════
                try
                {
                    foreach (var batch in unsaved.Chunk(50))
                    {
                        try
                        {
                            using var conn = new Microsoft.Data.SqlClient.SqlConnection(connString);
                            await conn.OpenAsync();

                            var guidList = string.Join(",",
                                batch.Select(p => $"'{p.GlobalPersonId}'"));

                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = $@"
                        SELECT GlobalPersonId, Id 
                        FROM UniquePersons WITH (NOLOCK) 
                        WHERE GlobalPersonId IN ({guidList})";
                            cmd.CommandTimeout = 5;

                            using var reader = await cmd.ExecuteReaderAsync();
                            while (await reader.ReadAsync())
                            {
                                var globalId = reader.GetGuid(0);
                                var dbId = reader.GetInt32(1);
                                if (dbId > 0)
                                {
                                    OnIdentitySavedToDatabase(globalId, dbId);
                                    reconciled++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("💾 Reconcile batch failed: {Err}", ex.Message);
                        }

                        await Task.Delay(300);
                    }

                    // Remove reconciled from unsaved list
                    if (reconciled > 0)
                    {
                        unsaved = unsaved
                            .Where(p => !IsIdentitySavedToDb(p.GlobalPersonId))
                            .ToList();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("💾 Reconcile failed: {Err}", ex.Message);
                }

                // Nothing left to save?
                if (unsaved.Count == 0)
                {
                    if (reconciled > 0)
                    {
                        _logger.LogWarning(
                            "💾 Flush complete: {Reconciled} reconciled (already in DB), 0 needed saving",
                            reconciled);
                    }
                    return;
                }

                // ═══════════════════════════════════════════════════════════════
                // PHASE 2: SAVE truly missing persons + GET their DbIds back
                //          Same pattern as your original working code
                // ═══════════════════════════════════════════════════════════════
                foreach (var batch in unsaved.Chunk(50))
                {
                    try
                    {
                        var personsJson = System.Text.Json.JsonSerializer.Serialize(
                            batch.Select(p => new
                            {
                                GlobalPersonId = p.GlobalPersonId.ToString(),
                                FeatureVector = string.Join(",", p.Features),
                                CameraId = p.CameraId,
                                FirstSeen = p.FirstSeen.ToString("o")
                            }));

                        using var conn = new Microsoft.Data.SqlClient.SqlConnection(connString);
                        await conn.OpenAsync();

                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
SET NOCOUNT ON;

-- Insert truly new persons
INSERT INTO UniquePersons WITH (ROWLOCK)
    (GlobalPersonId, FirstSeenAt, LastSeenAt, 
     FirstSeenCameraId, LastSeenCameraId, 
     TotalSightings, IsActive)
SELECT 
    TRY_CAST(j.GlobalPersonId AS UNIQUEIDENTIFIER),
    TRY_CAST(j.FirstSeen AS DATETIME2),
    TRY_CAST(j.FirstSeen AS DATETIME2),
    j.CameraId,
    j.CameraId,
    1,
    1
FROM OPENJSON(@Json) WITH (
    GlobalPersonId NVARCHAR(50),
    FeatureVector NVARCHAR(MAX),
    CameraId INT,
    FirstSeen NVARCHAR(50)
) j
WHERE NOT EXISTS (
    SELECT 1 FROM UniquePersons up WITH (NOLOCK)
    WHERE up.GlobalPersonId = TRY_CAST(j.GlobalPersonId AS UNIQUEIDENTIFIER)
);

-- Insert features for new persons (TRY/CATCH handles race)
BEGIN TRY
INSERT INTO UniquePersonFeatures WITH (ROWLOCK) (GlobalPersonId, FeatureVector)
SELECT 
    TRY_CAST(j.GlobalPersonId AS UNIQUEIDENTIFIER), 
    j.FeatureVector
FROM OPENJSON(@Json) WITH (
    GlobalPersonId NVARCHAR(50),
    FeatureVector NVARCHAR(MAX)
) j
WHERE j.FeatureVector IS NOT NULL 
    AND LEN(j.FeatureVector) > 100
    AND EXISTS (
        SELECT 1 FROM UniquePersons up WITH (NOLOCK)
        WHERE up.GlobalPersonId = TRY_CAST(j.GlobalPersonId AS UNIQUEIDENTIFIER)
    )
    AND NOT EXISTS (
        SELECT 1 FROM UniquePersonFeatures upf
        WHERE upf.GlobalPersonId = TRY_CAST(j.GlobalPersonId AS UNIQUEIDENTIFIER)
    );
END TRY
BEGIN CATCH
END CATCH

-- Return DbIds for all persons in batch
SELECT up.GlobalPersonId, up.Id
FROM OPENJSON(@Json) WITH (
    GlobalPersonId NVARCHAR(50)
) j
INNER JOIN UniquePersons up WITH (NOLOCK)
    ON up.GlobalPersonId = TRY_CAST(j.GlobalPersonId AS UNIQUEIDENTIFIER);";

                        cmd.CommandTimeout = 30;
                        cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@Json", personsJson));

                        using var reader = await cmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            try
                            {
                                var globalId = reader.GetGuid(0);
                                var dbId = reader.GetInt32(1);
                                if (dbId > 0)
                                {
                                    OnIdentitySavedToDatabase(globalId, dbId);
                                    saved++;
                                }
                            }
                            catch { }
                        }

                        await Task.Delay(200);
                    }
                    catch (Exception ex)
                    {
                        failed += batch.Length;
                        _logger.LogWarning("💾 Batch save failed: {Err}", ex.Message);
                        await Task.Delay(500);
                    }
                }

                _logger.LogWarning(
                    "💾 Flush complete: {Reconciled} reconciled, {Saved} saved, {Failed} failed",
                    reconciled, saved, failed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💾 Flush failed");
            }
        }
        #endregion

        #region Public Query Methods

        // ✅ UPDATE existing method:
        public void OnIdentitySavedToDatabase(Guid personId, int dbId)
        {
            if (_globalIdentities.TryGetValue(personId, out var identity))
            {
                identity.DbId = dbId;
                identity.IsFromDatabase = true;

                // ✅ NEW: Clear pending save flag
                _pendingSave.TryRemove(personId, out _);
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

        public int GetCameraIdentityCount(int cameraId)
        {
            return _globalIdentities.Values.Count(i =>
            {
                lock (i.SyncLock)
                {
                    return i.SeenOnCameras.Contains(cameraId) &&
                           _confirmedPersons.ContainsKey(i.GlobalPersonId);
                }
            });
        }

        public int GetGlobalUniqueCount() => _totalConfirmedEver;

        public (int Total, int Confirmed, int HighConfidence) GetDetailedCounts()
        {
            var total = _globalIdentities.Count;
            var confirmed = _totalConfirmedEver;
            var highConf = _globalIdentities.Values.Count(i =>
            {
                lock (i.SyncLock)
                {
                    return _confirmedPersons.ContainsKey(i.GlobalPersonId) &&
                           (i.IsConfirmedByConfidence || i.SeenOnCameras.Count > 1 || i.MatchCount >= 2);
                }
            });
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
                .Where(kvp =>
                    !kvp.Value.IsFromDatabase &&                          // Not from DB
                    kvp.Value.DbId == 0 &&                                // ✅ NEW: Not yet saved to DB
                    !_confirmedPersons.ContainsKey(kvp.Key) &&            // Not confirmed
                    kvp.Value.LastSeen < threshold)                       // Not seen recently
                .Select(kvp => kvp.Key)
                .ToList();

            // ✅ NEW: Separately handle confirmed but expired identities
            var confirmedToRemove = _globalIdentities
                .Where(kvp =>
                    kvp.Value.DbId > 0 &&                                 // Already saved to DB
                    !kvp.Value.IsCurrentlyActive &&                        // Not active right now
                    kvp.Value.LastSeen < threshold &&                      // Not seen recently
                    _confirmedPersons.ContainsKey(kvp.Key))                // Was confirmed
                .Select(kvp => kvp.Key)
                .ToList();

            // Remove unconfirmed, unsaved identities (these are noise)
            foreach (var id in toRemove)
            {
                _globalIdentities.TryRemove(id, out _);
                _entryTracking.TryRemove(id, out _);
            }

            // Remove confirmed identities that are saved to DB and expired
            // (they're safe in DB, just freeing memory)
            foreach (var id in confirmedToRemove)
            {
                _globalIdentities.TryRemove(id, out _);
                _confirmedPersons.TryRemove(id, out _); // 🆕 FIX: Now safe to remove
                _entryTracking.TryRemove(id, out _);
            }

            // 🆕 NEW: Cap _confirmedPersons to prevent unbounded growth
            if (_confirmedPersons.Count > 5000)
            {
                var idsToClean = _confirmedPersons.Keys
                    .Where(id => !_globalIdentities.ContainsKey(id))
                    .ToList();

                foreach (var id in idsToClean)
                    _confirmedPersons.TryRemove(id, out _);

                _logger.LogInformation("🧹 Cleaned {Count} orphaned confirmed entries", idsToClean.Count);
            }

            // 🆕 NEW: Cleanup _sessionPersons
            var staleSession = _sessionPersons
                .Where(kvp => kvp.Value < threshold)
                .Select(kvp => kvp.Key).ToList();
            foreach (var id in staleSession)
                _sessionPersons.TryRemove(id, out _);

            // 🆕 NEW: Cleanup _pendingSave (orphaned entries)
            var stalePending = _pendingSave.Keys
                .Where(id => !_globalIdentities.ContainsKey(id))
                .ToList();
            foreach (var id in stalePending)
                _pendingSave.TryRemove(id, out _);

            // 🆕 NEW: Trim confidence histories
            var historyWindow = DateTime.UtcNow.AddSeconds(-_settings.FastWalkerTimeWindowSeconds);
            foreach (var identity in _globalIdentities.Values)
            {
                lock (identity.SyncLock)
                {
                    if (identity.ConfidenceHistory.Count > 20)
                    {
                        identity.ConfidenceHistory.RemoveAll(r => r == null || r.Timestamp < historyWindow);
                    }
                }
            }

            // Cleanup stale trackers
            var staleTrackers = _trackStability
                .Where(kvp => (DateTime.UtcNow - kvp.Value.LastUpdate).TotalSeconds > 10)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in staleTrackers)
            {
                _trackStability.TryRemove(key, out _);
            }

            // Cleanup camera active persons
            foreach (var (_, activeDict) in _cameraActivePersons)
            {
                var expired = activeDict
                    .Where(kvp => kvp.Value < threshold)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var id in expired)
                    activeDict.TryRemove(id, out _);
            }

            if (toRemove.Count > 0 || confirmedToRemove.Count > 0)
            {
                _logger.LogDebug(
                    "🧹 Cleaned {Unconfirmed} unconfirmed + {Confirmed} confirmed (saved in DB) identities",
                    toRemove.Count, confirmedToRemove.Count);
            }
        }

        public void ClearAllIdentities()
        {
            _globalIdentities.Clear();
            _confirmedPersons.Clear();
            _totalConfirmedEver = 0;
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
        private int _todayConfirmedCount = 0;
        private int _startupConfirmedCount = 0;
        private DateTime _todayCounterDate = DateTime.UtcNow.Date;
        public int GetTodayUniqueCount()
        {
            var today = DateTime.UtcNow.Date;

            if (today != _todayCounterDate)
            {
                // ✅ Midnight: flush ALL unsaved (maxToFlush = 0)
                _ = FlushUnsavedToDatabaseAsync(0);

                _todayCounterDate = today;
                _todayConfirmedCount = 0;
                _cachedTodayCount = 0;
                _startupConfirmedCount = 0;
                _lastTodayCountUpdate = DateTime.MinValue;
            }

            if ((DateTime.UtcNow - _lastTodayCountUpdate).TotalSeconds > 120
                && !_isRefreshingTodayCount)
            {
                _isRefreshingTodayCount = true;
                _ = RefreshTodayCountAsync();
            }

            return _cachedTodayCount + (_todayConfirmedCount - _startupConfirmedCount);
        }

        private float[]? ParseFeatureVector(string? featureData)
        {
            if (string.IsNullOrEmpty(featureData)) return null;

            try
            {
                var parts = featureData.Split(',');

                // ✅ NEW: Validate dimension before parsing
                if (parts.Length != _settings.FeatureVectorDimension) return null;

                var features = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!float.TryParse(parts[i].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out features[i]))
                    {
                        return null;
                    }
                }
                return features;
            }
            catch
            {
                return null;
            }
        }


        private async Task RefreshTodayCountAsync()
        {
            try
            {
                var connString = "Server=DESKTOP-QML0799;Database=DetectionContext;" +
                    "Trusted_Connection=True;TrustServerCertificate=True;" +
                    "Connection Timeout=3;Command Timeout=10;" +
                    "Pooling=true;Max Pool Size=2;Min Pool Size=0;" +
                    "Application Name=TodayCount;";

                using var connection = new Microsoft.Data.SqlClient.SqlConnection(connString);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await connection.OpenAsync(cts.Token);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
    SELECT COUNT(*)
    FROM UniquePersons WITH (NOLOCK)
    WHERE FirstSeenAt >= CAST(SYSUTCDATETIME() AS DATE)
    OPTION (MAXDOP 1)";
                cmd.CommandTimeout = 10;

                var result = await cmd.ExecuteScalarAsync(cts.Token);
                var dbCount = Convert.ToInt32(result ?? 0);

                if (dbCount > 0)
                {
                    // ✅ Reset baseline to DB count
                    _cachedTodayCount = dbCount;
                    _startupConfirmedCount = _todayConfirmedCount;
                }

                _lastTodayCountUpdate = DateTime.UtcNow;
            }
            catch (Exception)
            {
                // Silent fail — formula keeps counting from last known DB count
                _lastTodayCountUpdate = DateTime.UtcNow.AddSeconds(60);
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