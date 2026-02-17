// PersonDetection.Application/Configuration/DetectionSettings.cs
namespace PersonDetection.Application.Configuration
{
    public class DetectionSettings
    {
        public const string SectionName = "DetectionConfig";

        public float ConfidenceThreshold { get; set; } = 0.30f;
        public float NmsThreshold { get; set; } = 0.45f;
        public int MinWidth { get; set; } = 15;
        public int MinHeight { get; set; } = 30;
        public int ModelInputSize { get; set; } = 640;
        public string YoloModelPath { get; set; } = "Models/yolo11s.onnx";
        public string ReIdModelPath { get; set; } = "Models/osnet_x1_0.onnx";
        public bool UseGpu { get; set; } = true;
    }

    public class IdentitySettings
    {
        public const string SectionName = "IdentityConfig";

        // ═══════════════════════════════════════════════════════════════
        // MATCHING THRESHOLDS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Maximum distance for DEFINITE match (same person for sure)
        /// Below this = reuse existing ID
        /// </summary>
        public float MinDistanceForNewIdentity { get; set; } = 0.30f;

        /// <summary>
        /// Maximum distance for ANY match consideration
        /// Between MinDistanceForNewIdentity and this = AMBIGUOUS → NEW ID
        /// Above this = definitely different person → NEW ID
        /// </summary>
        public float DistanceThreshold { get; set; } = 0.50f;

        /// <summary>
        /// Threshold for global cross-camera matching
        /// </summary>
        public float GlobalMatchThreshold { get; set; } = 0.45f;

        /// <summary>
        /// Similarity threshold (1 - distance) for matching
        /// </summary>
        public float SimilarityThreshold { get; set; } = 0.65f;

        /// <summary>
        /// Minimum ratio between best and second-best match
        /// </summary>
        public float MinSeparationRatio { get; set; } = 1.10f;

        // ═══════════════════════════════════════════════════════════════
        // TEMPORAL MATCHING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Enable time-based matching constraints
        /// </summary>
        public bool EnableTemporalMatching { get; set; } = true;

        /// <summary>
        /// Maximum seconds since last seen for "active" match (no penalty)
        /// </summary>
        public int MaxSecondsForActiveMatch { get; set; } = 30;

        /// <summary>
        /// Maximum minutes since last seen for "recent" match (small penalty)
        /// </summary>
        public int MaxMinutesForRecentMatch { get; set; } = 5;

        /// <summary>
        /// Distance penalty added for stale database matches
        /// </summary>
        public float PenaltyForStaleMatch { get; set; } = 0.20f;

        /// <summary>
        /// Require person to have been seen recently to match
        /// </summary>
        public bool RequireRecentActivityForMatch { get; set; } = true;

        // ═══════════════════════════════════════════════════════════════
        // ENTRY ZONE DETECTION
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Enable entry zone detection for new person identification
        /// </summary>
        public bool EnableEntryZoneDetection { get; set; } = true;

        /// <summary>
        /// Percentage of frame edges considered "entry zone"
        /// </summary>
        public int EntryZoneMarginPercent { get; set; } = 15;

        /// <summary>
        /// Distance bonus subtracted from threshold in entry zone
        /// Makes it harder to match (more new identities) in entry zones
        /// </summary>
        public float NewPersonBonusDistance { get; set; } = 0.10f;

        // ═══════════════════════════════════════════════════════════════
        // MATCH STABILITY
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Number of consecutive frames needed to change identity
        /// </summary>
        public int MatchStabilityFrames { get; set; } = 2;

        // ═══════════════════════════════════════════════════════════════
        // CONFIRMATION SETTINGS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Enable confidence-based confirmation
        /// </summary>
        public bool EnableConfidenceBasedConfirmation { get; set; } = true;

        /// <summary>
        /// Minimum confidence for a detection to count toward confirmation
        /// </summary>
        public float MinConfidenceForConfirmation { get; set; } = 0.40f;

        /// <summary>
        /// Number of high-confidence detections needed for confirmation
        /// </summary>
        public int MinHighConfidenceDetections { get; set; } = 2;

        /// <summary>
        /// Time window for fast walker confirmation (seconds)
        /// </summary>
        public float FastWalkerTimeWindowSeconds { get; set; } = 5.0f;

        /// <summary>
        /// Enable fast walker mode (single-frame confirmation)
        /// </summary>
        public bool EnableFastWalkerMode { get; set; } = true;

        /// <summary>
        /// Number of frames needed to confirm identity (normal walkers)
        /// </summary>
        public int ConfirmationMatchCount { get; set; } = 2;

        /// <summary>
        /// Treat ambiguous matches as confirmed (create new ID)
        /// </summary>
        public bool AmbiguousMatchAsConfirmed { get; set; } = true;

        /// <summary>
        /// Confidence threshold for INSTANT confirmation (single frame)
        /// </summary>
        public float InstantConfirmConfidence { get; set; } = 0.50f;

        /// <summary>
        /// Very high confidence threshold (confirms even without features)
        /// </summary>
        public float VeryHighConfidenceThreshold { get; set; } = 0.60f;

        // ═══════════════════════════════════════════════════════════════
        // CACHE & MEMORY
        // ═══════════════════════════════════════════════════════════════

        public int CacheExpirationMinutes { get; set; } = 30;
        public int MaxIdentitiesInMemory { get; set; } = 1000;
        public bool UpdateVectorOnMatch { get; set; } = false;
        public bool UseAdaptiveThreshold { get; set; } = false;
        public bool RequireMinimumSeparation { get; set; } = false;

        // ═══════════════════════════════════════════════════════════════
        // CROP SIZE
        // ═══════════════════════════════════════════════════════════════

        public int MinCropWidth { get; set; } = 20;
        public int MinCropHeight { get; set; } = 40;

        /// <summary>
        /// Minimum crop area multiplier (0.4 = 40% of MinCropWidth * MinCropHeight)
        /// </summary>
        public float MinCropAreaMultiplier { get; set; } = 0.4f;

        /// <summary>
        /// Minimum crop dimension multiplier (0.7 = 70% of MinCropWidth/Height)
        /// </summary>
        public float MinCropDimensionMultiplier { get; set; } = 0.7f;

        // ═══════════════════════════════════════════════════════════════
        // GLOBAL MATCHING
        // ═══════════════════════════════════════════════════════════════

        public bool EnableGlobalMatching { get; set; } = true;
        public bool LoadFromDatabaseOnStartup { get; set; } = true;
        public int DatabaseLoadHours { get; set; } = 2;

        // ═══════════════════════════════════════════════════════════════
        // CONSOLIDATION
        // ═══════════════════════════════════════════════════════════════

        public bool EnableIdentityConsolidation { get; set; } = false;
        public float ConsolidationThreshold { get; set; } = 0.10f;

        // ═══════════════════════════════════════════════════════════════
        // FEATURE VALIDATION
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Minimum variance for valid feature vector
        /// </summary>
        public float MinFeatureVariance { get; set; } = 0.000001f;

        /// <summary>
        /// Expected feature vector dimension
        /// </summary>
        public int FeatureVectorDimension { get; set; } = 512;

        /// <summary>
        /// Minimum confidence to proceed with ReID when size is small
        /// </summary>
        public float MinConfidenceForSmallDetection { get; set; } = 0.25f;

        /// <summary>
        /// Single frame instant confirmation threshold
        /// Any detection >= this confidence with features = instant unique
        /// </summary>
        public float SingleFrameInstantConfirmConfidence { get; set; } = 0.60f;
    }

    public class TrackingSettings
    {
        public const string SectionName = "TrackingConfig";

        // ═══════════════════════════════════════════════════════════════
        // SPATIAL TRACKING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Maximum pixel distance for normal walking speed
        /// </summary>
        public float NormalMaxDistance { get; set; } = 120f;

        /// <summary>
        /// Maximum pixel distance for fast walking speed
        /// </summary>
        public float FastWalkerMaxDistance { get; set; } = 250f;

        /// <summary>
        /// Bonus distance when features match well
        /// </summary>
        public float FeatureMatchBonusDistance { get; set; } = 80f;

        /// <summary>
        /// Feature distance threshold for "good match" bonus
        /// </summary>
        public float FeatureMatchThreshold { get; set; } = 0.35f;

        /// <summary>
        /// Score multiplier when predicted position matches
        /// </summary>
        public float PredictionMatchBonus { get; set; } = 0.8f;

        // ═══════════════════════════════════════════════════════════════
        // STABLE TRACK SETTINGS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Minimum frames for a track to become "stable" (normal walker)
        /// </summary>
        public int StableTrackMinFrames { get; set; } = 2;

        /// <summary>
        /// Minimum confidence for a track to become "stable" (normal walker)
        /// </summary>
        public float StableTrackMinConfidence { get; set; } = 0.45f;

        /// <summary>
        /// Minimum frames for fast walker to become stable
        /// </summary>
        public int FastWalkerStableFrames { get; set; } = 1;

        /// <summary>
        /// Minimum confidence for fast walker to become stable
        /// </summary>
        public float FastWalkerStableConfidence { get; set; } = 0.35f;

        // ═══════════════════════════════════════════════════════════════
        // VELOCITY TRACKING
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Smoothing factor for velocity (0-1, higher = more responsive)
        /// </summary>
        public float VelocitySmoothingAlpha { get; set; } = 0.6f;

        /// <summary>
        /// Minimum speed (pixels/frame) to be considered "fast walker"
        /// </summary>
        public float FastWalkerSpeedThreshold { get; set; } = 60f;

        // ═══════════════════════════════════════════════════════════════
        // TRACK CLEANUP
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Seconds before normal track is cleaned up
        /// </summary>
        public float NormalTrackTimeoutSeconds { get; set; } = 2.0f;

        /// <summary>
        /// Seconds before fast walker track is cleaned up
        /// </summary>
        public float FastWalkerTrackTimeoutSeconds { get; set; } = 3.0f;

        // ═══════════════════════════════════════════════════════════════
        // CONFIRMATION
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Minimum confidence for 2-frame confirmation
        /// </summary>
        public float TwoFrameConfirmMinConfidence { get; set; } = 0.45f;

        /// <summary>
        /// Minimum confidence for high-conf single-frame confirmation
        /// </summary>
        public float HighConfSingleFrameThreshold { get; set; } = 0.60f;

        /// <summary>
        /// Maximum age (frames) for pending confirmation
        /// </summary>
        public int MaxPendingConfirmationAge { get; set; } = 8;

        /// <summary>
        /// Maximum time (seconds) for pending confirmation
        /// </summary>
        public float MaxPendingConfirmationSeconds { get; set; } = 3.0f;

        // ═══════════════════════════════════════════════════════════════
        // LEGACY SETTINGS (for compatibility)
        // ═══════════════════════════════════════════════════════════════

        public int MaxAge { get; set; } = 30;
        public int MinHits { get; set; } = 2;
        public float IouThreshold { get; set; } = 0.3f;
        public float MaxPositionDistance { get; set; } = 200f;
        public float VelocityWeight { get; set; } = 0.4f;
        public bool UseKalmanPrediction { get; set; } = true;
        /// <summary>
        /// Single frame instant confirmation threshold (bypass all other checks)
        /// </summary>
        public float SingleFrameInstantThreshold { get; set; } = 0.60f;
    }

    public class StreamingSettings
    {
        public const string SectionName = "StreamingConfig";

        public int FrameBufferSize { get; set; } = 2;
        public int TargetFps { get; set; } = 25;
        public int ReIdEveryNFrames { get; set; } = 1;
        public int ReconnectDelayMs { get; set; } = 3000;
        public int JpegQuality { get; set; } = 80;
        public int ProcessingIntervalMs { get; set; } = 40;
        public int DetectionIntervalMs { get; set; } = 50;
        public int ResizeWidth { get; set; } = 1280;
        public int ResizeHeight { get; set; } = 720;
        public int SkipFrames { get; set; } = 0;
        public bool UseHardwareAcceleration { get; set; } = true;

        // ═══════════════════════════════════════════════════════════════
        // OVERLAY SETTINGS
        // ═══════════════════════════════════════════════════════════════

        public bool EnableFastOverlay { get; set; } = true;
        public double OverlayFontScale { get; set; } = 0.5;
        public int OverlayThickness { get; set; } = 2;
        public bool ShowConfidence { get; set; } = true;
        public bool ShowTrackId { get; set; } = true;


        // ADD these properties to your existing StreamingSettings class

        /// <summary>
        /// Maximum consecutive frame read errors before considering stream disconnected
        /// </summary>
        public int MaxConsecutiveFrameErrors { get; set; } = 30;

        /// <summary>
        /// Enable automatic reconnection when stream disconnects during operation
        /// </summary>
        public bool EnableAutoReconnect { get; set; } = true;

        /// <summary>
        /// Maximum number of reconnection attempts (0 = unlimited)
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 5;

        /// <summary>
        /// Initial delay before first reconnection attempt (ms)
        /// </summary>
        public int InitialReconnectDelayMs { get; set; } = 1000;

        /// <summary>
        /// Maximum delay between reconnection attempts (ms)
        /// </summary>
        public int MaxReconnectDelayMs { get; set; } = 30000;

        /// <summary>
        /// Multiplier for exponential backoff
        /// </summary>
        public double ReconnectBackoffMultiplier { get; set; } = 2.0;

        /// <summary>
        /// Interval for stream health check notifications (ms)
        /// </summary>
        public int StreamHealthCheckIntervalMs { get; set; } = 5000;

        /// <summary>
        /// Timeout for initial connection (ms)
        /// </summary>
        public int ConnectionTimeoutMs { get; set; } = 10000;

        /// <summary>
        /// Timeout for frame read operations (ms)
        /// </summary>
        public int ReadTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// Notify frontend clients when stream status changes
        /// </summary>
        public bool NotifyClientsOnStatusChange { get; set; } = true;
    }


    public class SignalRSettings
    {
        public const string SectionName = "SignalRConfig";

        public int MaximumReceiveMessageSize { get; set; } = 102400;
        public int KeepAliveIntervalSeconds { get; set; } = 15;
        public int ClientTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Reconnection intervals for client (ms)
        /// </summary>
        public int[] ReconnectIntervalsMs { get; set; } = { 0, 2000, 5000, 10000, 30000 };

        /// <summary>
        /// Maximum reconnection attempts for client
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 10;

        /// <summary>
        /// Fallback delay when all retry intervals exhausted (ms)
        /// </summary>
        public int FallbackReconnectDelayMs { get; set; } = 5000;
    }
}