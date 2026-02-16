// PersonDetection.Infrastructure/Streaming/StreamProcessorFactory.cs
namespace PersonDetection.Infrastructure.Streaming
{
    using Microsoft.AspNetCore.SignalR;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using PersonDetection.API.Hubs;
    using PersonDetection.Application.Commands;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Services;
    using PersonDetection.Infrastructure.Detection;
    using PersonDetection.Infrastructure.ReId;
    using System.Collections.Concurrent;

    public class StreamProcessorFactory : IStreamProcessorFactory, IDisposable
    {
        private readonly ConcurrentDictionary<int, IStreamProcessor> _processors = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<StreamProcessorFactory> _logger;

        public StreamProcessorFactory(IServiceProvider serviceProvider, ILogger<StreamProcessorFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public IStreamProcessor Create(int cameraId, string url)
        {
            return _processors.GetOrAdd(cameraId, id =>
            {
                _logger.LogInformation("Creating stream processor for camera {Id}", id);

                // Get optional ReID engine (may be null if model not loaded)
                IReIdentificationEngine<OSNetConfig>? reidEngine = null;
                try
                {
                    reidEngine = _serviceProvider.GetService<IReIdentificationEngine<OSNetConfig>>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ReID engine not available");
                }

                return new CameraStreamProcessor(
                    id,
                    _serviceProvider.GetRequiredService<IDetectionEngine<YoloDetectionConfig>>(),
                    reidEngine,
                    _serviceProvider.GetRequiredService<IPersonIdentityMatcher>(),
                    _serviceProvider.GetRequiredService<IHubContext<DetectionHub>>(),
                    _serviceProvider,
                    _serviceProvider.GetRequiredService<IOptions<StreamingSettings>>(),
                    _serviceProvider.GetRequiredService<IOptions<DetectionSettings>>(),
                    _serviceProvider.GetRequiredService<IOptions<PersistenceSettings>>(),
                    _serviceProvider.GetRequiredService<IOptions<TrackingSettings>>(),      // ← ADD THIS
                    _serviceProvider.GetRequiredService<IOptions<IdentitySettings>>(),      // ← ADD THIS
                    _serviceProvider.GetRequiredService<ILogger<CameraStreamProcessor>>()
                );
            });
        }

        public IStreamProcessor? Get(int cameraId) =>
            _processors.TryGetValue(cameraId, out var p) ? p : null;

        public IReadOnlyDictionary<int, IStreamProcessor> GetAll() => _processors;


        public void Remove(int cameraId)
        {
            if (_processors.TryRemove(cameraId, out var p)) p.Dispose();
        }

        public void Dispose()
        {
            foreach (var p in _processors.Values) p.Dispose();
            _processors.Clear();
        }
    }
}