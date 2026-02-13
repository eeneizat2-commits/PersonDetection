// PersonDetection.Application/Configuration/DetectionSettings.cs
namespace PersonDetection.Application.Configuration
{
    public class DetectionSettings
    {
        public const string SectionName = "DetectionConfig";

        public float ConfidenceThreshold { get; set; } = 0.25f;
        public float NmsThreshold { get; set; } = 0.45f;
        public int MinWidth { get; set; } = 12;
        public int MinHeight { get; set; } = 25;
        public int ModelInputSize { get; set; } = 640;
        public string YoloModelPath { get; set; } = "Models/yolo11s.onnx";
        public string ReIdModelPath { get; set; } = "Models/osnet_x1_0.onnx";
        public bool UseGpu { get; set; } = true;
    }

    public class IdentitySettings
    {
        public const string SectionName = "IdentityConfig";

        // ═══════════════════════════════════════════════════════════════
        // MATCHING THRESHOLDS - STRICT (harder to match = more new)
        // ═══════════════════════════════════════════════════════════════
        public float DistanceThreshold { get; set; } = 0.25f;
        public float MinDistanceForNewIdentity { get; set; } = 0.15f;
        public float GlobalMatchThreshold { get; set; } = 0.20f;
        public float SimilarityThreshold { get; set; } = 0.80f;
        public float MinSeparationRatio { get; set; } = 1.05f;

        // ═══════════════════════════════════════════════════════════════
        // TEMPORAL MATCHING - AGGRESSIVE
        // ═══════════════════════════════════════════════════════════════
        public bool EnableTemporalMatching { get; set; } = true;
        public int MaxSecondsForActiveMatch { get; set; } = 15;
        public int MaxMinutesForRecentMatch { get; set; } = 1;
        public float PenaltyForStaleMatch { get; set; } = 0.50f;
        public bool RequireRecentActivityForMatch { get; set; } = true;

        // ═══════════════════════════════════════════════════════════════
        // ★★★ NEW: ONLY MATCH ACTIVE IDENTITIES ★★★
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// ONLY match against identities seen in the last N seconds
        /// Ignores ALL database entries and old identities
        /// </summary>
        public bool OnlyMatchActiveIdentities { get; set; } = true;

        /// <summary>
        /// Timeout in seconds for an identity to be considered "active"
        /// </summary>
        public int ActiveIdentityTimeoutSeconds { get; set; } = 20;

        /// <summary>
        /// When ambiguous (multiple close matches), create NEW identity instead of picking one
        /// </summary>
        public bool TreatAmbiguousAsNew { get; set; } = true;

        // ═══════════════════════════════════════════════════════════════
        // ENTRY ZONE DETECTION
        // ═══════════════════════════════════════════════════════════════
        public bool EnableEntryZoneDetection { get; set; } = true;
        public int EntryZoneMarginPercent { get; set; } = 25;
        public float NewPersonBonusDistance { get; set; } = 0.15f;

        // ═══════════════════════════════════════════════════════════════
        // MATCH STABILITY
        // ═══════════════════════════════════════════════════════════════
        public int MatchStabilityFrames { get; set; } = 1;

        // ═══════════════════════════════════════════════════════════════
        // FAST WALKER / INSTANT CONFIRMATION
        // ═══════════════════════════════════════════════════════════════
        public bool EnableConfidenceBasedConfirmation { get; set; } = true;
        public float MinConfidenceForConfirmation { get; set; } = 0.25f;
        public int MinHighConfidenceDetections { get; set; } = 1;
        public float FastWalkerTimeWindowSeconds { get; set; } = 10.0f;
        public bool EnableFastWalkerMode { get; set; } = true;
        public int ConfirmationMatchCount { get; set; } = 1;
        public bool AmbiguousMatchAsConfirmed { get; set; } = true;
        public float InstantConfirmConfidence { get; set; } = 0.30f;

        // ═══════════════════════════════════════════════════════════════
        // CACHE & MEMORY
        // ═══════════════════════════════════════════════════════════════
        public int CacheExpirationMinutes { get; set; } = 10;
        public int MaxIdentitiesInMemory { get; set; } = 500;
        public bool UpdateVectorOnMatch { get; set; } = false;
        public bool UseAdaptiveThreshold { get; set; } = false;
        public bool RequireMinimumSeparation { get; set; } = false;

        // ═══════════════════════════════════════════════════════════════
        // CROP SIZE
        // ═══════════════════════════════════════════════════════════════
        public int MinCropWidth { get; set; } = 12;
        public int MinCropHeight { get; set; } = 25;

        // ═══════════════════════════════════════════════════════════════
        // GLOBAL MATCHING - DISABLED
        // ═══════════════════════════════════════════════════════════════
        public bool EnableGlobalMatching { get; set; } = false;
        public bool LoadFromDatabaseOnStartup { get; set; } = false;
        public int DatabaseLoadHours { get; set; } = 0;

        // ═══════════════════════════════════════════════════════════════
        // CONSOLIDATION
        // ═══════════════════════════════════════════════════════════════
        public bool EnableIdentityConsolidation { get; set; } = false;
        public float ConsolidationThreshold { get; set; } = 0.10f;
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
        public int MinHits { get; set; } = 1;
        public float IouThreshold { get; set; } = 0.25f;
        public float MaxPositionDistance { get; set; } = 250f;
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