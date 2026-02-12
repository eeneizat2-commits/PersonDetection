// PersonDetection.Application/Configuration/DetectionSettings.cs
namespace PersonDetection.Application.Configuration
{
    public class DetectionSettings
    {
        public const string SectionName = "DetectionConfig";

        public float ConfidenceThreshold { get; set; } = 0.35f;
        public float NmsThreshold { get; set; } = 0.45f;
        public int MinWidth { get; set; } = 15;
        public int MinHeight { get; set; } = 30;
        public int ModelInputSize { get; set; } = 640;
        public string YoloModelPath { get; set; } = "Models/yolo11s.onnx";
        public string ReIdModelPath { get; set; } = "Models/osnet_x1_0.onnx";
        public bool UseGpu { get; set; } = true;
    }

    // In DetectionSettings.cs
    // In DetectionSettings.cs
    public class IdentitySettings
    {
        public const string SectionName = "IdentityConfig";

        // Matching thresholds
        public float DistanceThreshold { get; set; } = 0.30f;
        public float MinDistanceForNewIdentity { get; set; } = 0.10f;
        public float GlobalMatchThreshold { get; set; } = 0.25f;  // Stricter for cross-camera
        public float SimilarityThreshold { get; set; } = 0.75f;
        public float MinSeparationRatio { get; set; } = 1.25f;

        // Cache settings
        public int CacheExpirationMinutes { get; set; } = 60;
        public int MaxIdentitiesInMemory { get; set; } = 500;

        // Feature vector settings
        public bool UpdateVectorOnMatch { get; set; } = false;
        public bool UseAdaptiveThreshold { get; set; } = false;
        public bool RequireMinimumSeparation { get; set; } = true;

        // Crop size for ReID
        public int MinCropWidth { get; set; } = 32;
        public int MinCropHeight { get; set; } = 64;

        // GLOBAL cross-camera matching
        public bool EnableGlobalMatching { get; set; } = true;
        public bool LoadFromDatabaseOnStartup { get; set; } = true;
        public int DatabaseLoadHours { get; set; } = 24;  // Load persons from last 24 hours

        // Consolidation
        public bool EnableIdentityConsolidation { get; set; } = true;
        public float ConsolidationThreshold { get; set; } = 0.08f;

        // Confirmation
        public int ConfirmationMatchCount { get; set; } = 2;
        public bool AmbiguousMatchAsConfirmed { get; set; } = false;
    }

    public class TrackingSettings
    {
        public const string SectionName = "TrackingConfig";

        public int MaxAge { get; set; } = 30;
        public int MinHits { get; set; } = 3;
        public float IouThreshold { get; set; } = 0.3f;
        public float MaxPositionDistance { get; set; } = 150f;
        public float VelocityWeight { get; set; } = 0.4f;
        public bool UseKalmanPrediction { get; set; } = true;
    }

    public class StreamingSettings
    {
        public const string SectionName = "StreamingConfig";

        public int FrameBufferSize { get; set; } = 3;
        public int TargetFps { get; set; } = 30;
        public int ReIdEveryNFrames { get; set; } = 2;
        public int ReconnectDelayMs { get; set; } = 3000;
        public int MaxReconnectAttempts { get; set; } = 5;
        public int JpegQuality { get; set; } = 75;
        public int ProcessingIntervalMs { get; set; } = 33;
        public int DetectionIntervalMs { get; set; } = 66;
        public int ResizeWidth { get; set; } = 960;
        public int ResizeHeight { get; set; } = 540;
        public int SkipFrames { get; set; } = 1;
        public bool UseHardwareAcceleration { get; set; } = true;
    }

    public class SignalRSettings
    {
        public const string SectionName = "SignalRConfig";

        public int MaximumReceiveMessageSize { get; set; } = 102400;
        public int KeepAliveIntervalSeconds { get; set; } = 15;
        public int ClientTimeoutSeconds { get; set; } = 30;
    }
}