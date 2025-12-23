namespace PersonDetection.Infrastructure.Detection
{
    using Microsoft.ML.OnnxRuntime;
    using Microsoft.ML.OnnxRuntime.Tensors;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.ValueObjects;

    public abstract class GenericOnnxDetectionEngine<TConfig> : IDetectionEngine<TConfig> where TConfig : class
    {
        protected readonly InferenceSession _session;
        protected readonly ILogger _logger;

        public abstract string Name { get; }
        public bool IsGpuAccelerated { get; protected set; }

        protected GenericOnnxDetectionEngine(string modelPath, bool useGpu, ILogger logger)
        {
            _logger = logger;

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_PARALLEL,
                IntraOpNumThreads = Environment.ProcessorCount,
                InterOpNumThreads = 1
            };

            if (useGpu)
            {
                try
                {
                    options.AppendExecutionProvider_CUDA(0);
                    IsGpuAccelerated = true;
                    _logger.LogInformation("{EngineName}: GPU acceleration enabled", Name);
                }
                catch
                {
                    _logger.LogWarning("{EngineName}: GPU not available, using CPU", Name);
                }
            }

            _session = new InferenceSession(modelPath, options);
        }

        public async Task<List<DetectedPerson>> DetectAsync(byte[] imageData, TConfig config, CancellationToken ct = default)
        {
            using var stream = new MemoryStream(imageData);
            return await DetectAsync(stream, config, ct);
        }

        public abstract Task<List<DetectedPerson>> DetectAsync(Stream imageStream, TConfig config, CancellationToken ct = default);

        protected abstract DenseTensor<float> PreprocessImage(byte[] imageData, TConfig config);
        protected abstract List<DetectedPerson> PostprocessOutput(float[] output, TConfig config, int originalWidth, int originalHeight);

        protected List<DetectedPerson> ApplyNMS(List<DetectedPerson> detections, float threshold)
        {
            if (detections.Count == 0) return detections;

            var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
            var selected = new List<DetectedPerson>();
            var active = Enumerable.Repeat(true, sorted.Count).ToArray();

            for (int i = 0; i < sorted.Count; i++)
            {
                if (!active[i]) continue;

                selected.Add(sorted[i]);

                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (!active[j]) continue;

                    var iou = sorted[i].BoundingBox.IoU(sorted[j].BoundingBox);
                    if (iou > threshold)
                    {
                        active[j] = false;
                    }
                }
            }

            return selected;
        }
    }

}