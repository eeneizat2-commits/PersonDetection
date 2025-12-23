namespace PersonDetection.Application.Interfaces
{
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.ValueObjects;

    public interface IDetectionEngine<TConfig> where TConfig : class
    {
        Task<List<DetectedPerson>> DetectAsync(byte[] imageData, TConfig config, CancellationToken ct = default);
        Task<List<DetectedPerson>> DetectAsync(Stream imageStream, TConfig config, CancellationToken ct = default);
        string Name { get; }
        bool IsGpuAccelerated { get; }
    }

    public interface IReIdentificationEngine<TConfig> where TConfig : class
    {
        Task<FeatureVector> ExtractFeaturesAsync(byte[] imageData, BoundingBox roi, TConfig config, CancellationToken ct = default);
        Task<List<FeatureVector>> ExtractFeaturesBatchAsync(List<(byte[] imageData, BoundingBox roi)> batch, TConfig config, CancellationToken ct = default);
        int VectorDimension { get; }
    }

    public interface IStreamProcessor : IDisposable
    {
        Task<bool> ConnectAsync(string url, CancellationToken ct = default);
        IAsyncEnumerable<byte[]> ReadFramesAsync(CancellationToken ct = default);
        IAsyncEnumerable<byte[]> GetAnnotatedFramesAsync(CancellationToken ct = default);
        bool IsConnected { get; }
        int CurrentPersonCount { get; }
    }
}
