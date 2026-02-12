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
        public float DistanceThreshold { get; set; } = 0.35f;
        public float MinDistanceForNewIdentity { get; set; } = 0.20f;
        public float GlobalMatchThreshold { get; set; } = 0.30f;
        public float SimilarityThreshold { get; set; } = 0.75f;
        public float MinSeparationRatio { get; set; } = 1.15f;

        // ═══════════════════════════════════════════════════════════════
        // TEMPORAL MATCHING (NEW - Key for walking people)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Enable time-based matching constraints
        /// </summary>
        public bool EnableTemporalMatching { get; set; } = true;

        /// <summary>
        /// Maximum seconds since last seen for "active" match (no penalty)
        /// </summary>
        public int MaxSecondsForActiveMatch { get; set; } = 60;

        /// <summary>
        /// Maximum minutes since last seen for "recent" match (small penalty)
        /// </summary>
        public int MaxMinutesForRecentMatch { get; set; } = 10;

        /// <summary>
        /// Distance penalty added for stale database matches
        /// </summary>
        public float PenaltyForStaleMatch { get; set; } = 0.15f;

        /// <summary>
        /// Require person to have been seen recently to match
        /// </summary>
        public bool RequireRecentActivityForMatch { get; set; } = true;

        // ═══════════════════════════════════════════════════════════════
        // ENTRY ZONE DETECTION (NEW - Detect new people entering frame)
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
        /// Bonus distance subtracted when person appears in entry zone
        /// (Makes it easier to create new identity for entering people)
        /// </summary>
        public float NewPersonBonusDistance { get; set; } = 0.08f;

        // ═══════════════════════════════════════════════════════════════
        // MATCH STABILITY (NEW - Prevent ID flipping)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Number of consecutive frames needed to change identity
        /// </summary>
        public int MatchStabilityFrames { get; set; } = 2;

        // ═══════════════════════════════════════════════════════════════
        // FAST WALKER CONFIRMATION
        // ═══════════════════════════════════════════════════════════════
        public bool EnableConfidenceBasedConfirmation { get; set; } = true;
        public float MinConfidenceForConfirmation { get; set; } = 0.55f;
        public int MinHighConfidenceDetections { get; set; } = 2;
        public float FastWalkerTimeWindowSeconds { get; set; } = 8.0f;
        public bool EnableFastWalkerMode { get; set; } = true;
        public int ConfirmationMatchCount { get; set; } = 2;
        public bool AmbiguousMatchAsConfirmed { get; set; } = true;

        // ═══════════════════════════════════════════════════════════════
        // CACHE & MEMORY
        // ═══════════════════════════════════════════════════════════════
        public int CacheExpirationMinutes { get; set; } = 30;
        public int MaxIdentitiesInMemory { get; set; } = 500;
        public bool UpdateVectorOnMatch { get; set; } = false;
        public bool UseAdaptiveThreshold { get; set; } = true;
        public bool RequireMinimumSeparation { get; set; } = false;

        // ═══════════════════════════════════════════════════════════════
        // CROP SIZE
        // ═══════════════════════════════════════════════════════════════
        public int MinCropWidth { get; set; } = 20;
        public int MinCropHeight { get; set; } = 40;

        // ═══════════════════════════════════════════════════════════════
        // GLOBAL MATCHING
        // ═══════════════════════════════════════════════════════════════
        public bool EnableGlobalMatching { get; set; } = true;
        public bool LoadFromDatabaseOnStartup { get; set; } = true;
        public int DatabaseLoadHours { get; set; } = 4; // Reduced from 24

        // ═══════════════════════════════════════════════════════════════
        // CONSOLIDATION
        // ═══════════════════════════════════════════════════════════════
        public bool EnableIdentityConsolidation { get; set; } = false;
        public float ConsolidationThreshold { get; set; } = 0.10f;

        /// <summary>
        /// Confidence threshold for INSTANT confirmation (single frame)
        /// Any detection above this = immediately confirmed as unique
        /// </summary>
        public float InstantConfirmConfidence { get; set; } = 0.40f;
    }

    public class StreamingSettings
    {
        public const string SectionName = "StreamingConfig";

        public int FrameBufferSize { get; set; } = 2;
        public int TargetFps { get; set; } = 25;
        public int ReIdEveryNFrames { get; set; } = 1;
        public int ReconnectDelayMs { get; set; } = 3000;
        public int MaxReconnectAttempts { get; set; } = 5;
        public int JpegQuality { get; set; } = 80;
        public int ProcessingIntervalMs { get; set; } = 40;
        public int DetectionIntervalMs { get; set; } = 50;
        public int ResizeWidth { get; set; } = 1280;
        public int ResizeHeight { get; set; } = 720;
        public int SkipFrames { get; set; } = 0;
        public bool UseHardwareAcceleration { get; set; } = true;

        // ═══════════════════════════════════════════════════════════════
        // OVERLAY SETTINGS (NEW - Faster rendering)
        // ═══════════════════════════════════════════════════════════════
        public bool EnableFastOverlay { get; set; } = true;
        public double OverlayFontScale { get; set; } = 0.5;
        public int OverlayThickness { get; set; } = 2;
        public bool ShowConfidence { get; set; } = true;
        public bool ShowTrackId { get; set; } = true;
    }

    public class TrackingSettings
    {
        public const string SectionName = "TrackingConfig";

        public int MaxAge { get; set; } = 30;
        public int MinHits { get; set; } = 2;
        public float IouThreshold { get; set; } = 0.3f;
        public float MaxPositionDistance { get; set; } = 200f;
        public float VelocityWeight { get; set; } = 0.4f;
        public bool UseKalmanPrediction { get; set; } = true;
    }

    public class SignalRSettings
    {
        public const string SectionName = "SignalRConfig";

        public int MaximumReceiveMessageSize { get; set; } = 102400;
        public int KeepAliveIntervalSeconds { get; set; } = 15;
        public int ClientTimeoutSeconds { get; set; } = 30;
    }
}