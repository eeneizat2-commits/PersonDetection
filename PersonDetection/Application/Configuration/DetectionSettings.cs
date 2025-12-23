// PersonDetection.Application/Configuration/DetectionSettings.cs
namespace PersonDetection.Application.Configuration
{
    public class DetectionSettings
    {
        public const string SectionName = "DetectionConfig";

        public float ConfidenceThreshold { get; set; } = 0.4f;
        public float NmsThreshold { get; set; } = 0.45f;
        public int MinWidth { get; set; } = 10;
        public int MinHeight { get; set; } = 20;
        public int ModelInputSize { get; set; } = 640;
        public string YoloModelPath { get; set; } = "Models/yolo11s.onnx";
        public string ReIdModelPath { get; set; } = "Models/osnet_x1_0.onnx";
        public bool UseGpu { get; set; } = true;
    }

    public class IdentitySettings
    {
        public const string SectionName = "IdentityConfig";

        public float SimilarityThreshold { get; set; } = 0.70f;
        public int CacheExpirationMinutes { get; set; } = 30;
        public bool UpdateVectorOnMatch { get; set; } = true;
        public float VectorUpdateAlpha { get; set; } = 0.1f;
    }

    public class StreamingSettings
    {
        public const string SectionName = "StreamingConfig";

        public int FrameBufferSize { get; set; } = 3;
        public int TargetFps { get; set; } = 25;
        public int ReconnectDelayMs { get; set; } = 3000;
        public int MaxReconnectAttempts { get; set; } = 5;
        public int JpegQuality { get; set; } = 70;
        public int ProcessingIntervalMs { get; set; } = 40;
        public int DetectionIntervalMs { get; set; } = 100;  // Run detection every 100ms
        public int ResizeWidth { get; set; } = 640;
        public int ResizeHeight { get; set; } = 480;
        public int SkipFrames { get; set; } = 2;  // Process every Nth frame for detection
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