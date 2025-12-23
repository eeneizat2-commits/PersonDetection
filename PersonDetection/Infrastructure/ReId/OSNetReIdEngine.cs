// PersonDetection.Infrastructure/ReId/OSNetReIdEngine.cs
namespace PersonDetection.Infrastructure.ReId
{
    using Microsoft.Extensions.Logging;
    using Microsoft.ML.OnnxRuntime;
    using Microsoft.ML.OnnxRuntime.Tensors;
    using OpenCvSharp;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.ValueObjects;

    // Alias to resolve ambiguity
    using CvPoint = OpenCvSharp.Point;
    using CvSize = OpenCvSharp.Size;

    public class OSNetConfig
    {
        public int InputWidth { get; set; } = 128;
        public int InputHeight { get; set; } = 256;
        public float[] Mean { get; set; } = { 0.485f, 0.456f, 0.406f };
        public float[] Std { get; set; } = { 0.229f, 0.224f, 0.225f };
        public bool NormalizeOutput { get; set; } = true;
    }

    public class OSNetReIdEngine : IReIdentificationEngine<OSNetConfig>, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly ILogger<OSNetReIdEngine> _logger;
        private readonly string _inputName;
        private readonly int[] _inputShape;
        private bool _disposed;

        public int VectorDimension { get; private set; } = 512;
        public string Name => "OSNet-x1.0";

        // ✅ FIXED: Added private set
        public bool IsGpuAccelerated { get; private set; }

        public OSNetReIdEngine(string modelPath, bool useGpu, ILogger<OSNetReIdEngine> logger)
        {
            _logger = logger;

            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"ReID model not found: {modelPath}");

            var options = CreateSessionOptions(useGpu);
            _session = new InferenceSession(modelPath, options);

            // Get input metadata
            var inputMeta = _session.InputMetadata.First();
            _inputName = inputMeta.Key;
            _inputShape = inputMeta.Value.Dimensions;

            // Determine vector dimension from output
            var outputMeta = _session.OutputMetadata.First();
            if (outputMeta.Value.Dimensions.Length > 1)
            {
                VectorDimension = outputMeta.Value.Dimensions[1];
            }

            _logger.LogInformation("{Name} initialized. GPU: {Gpu}, Vector Dim: {Dim}",
                Name, IsGpuAccelerated, VectorDimension);
        }

        private SessionOptions CreateSessionOptions(bool useGpu)
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Environment.ProcessorCount / 2
            };

            if (useGpu)
            {
                try
                {
                    options.AppendExecutionProvider_CUDA(0);
                    IsGpuAccelerated = true;  // ✅ Now works
                    _logger.LogInformation("{Name}: CUDA GPU acceleration enabled", Name);
                }
                catch (Exception ex)
                {
                    IsGpuAccelerated = false;
                    _logger.LogWarning(ex, "{Name}: GPU not available, falling back to CPU", Name);
                }
            }

            return options;
        }

        public async Task<FeatureVector> ExtractFeaturesAsync(
            byte[] imageData,
            BoundingBox roi,
            OSNetConfig config,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                using var mat = Mat.FromImageData(imageData, ImreadModes.Color);
                using var cropped = CropAndResize(mat, roi, config);
                var tensor = PrepareInput(cropped, config);
                var features = RunInference(tensor);

                return config.NormalizeOutput
                    ? new FeatureVector(features).Normalize()
                    : new FeatureVector(features);
            }, ct);
        }

        public async Task<List<FeatureVector>> ExtractFeaturesBatchAsync(
            List<(byte[] imageData, BoundingBox roi)> batch,
            OSNetConfig config,
            CancellationToken ct = default)
        {
            if (batch.Count == 0)
                return new List<FeatureVector>();

            // Process in parallel for CPU, sequential for GPU (to avoid memory issues)
            if (IsGpuAccelerated)
            {
                var results = new List<FeatureVector>();
                foreach (var (imageData, roi) in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    var feature = await ExtractFeaturesAsync(imageData, roi, config, ct);
                    results.Add(feature);
                }
                return results;
            }
            else
            {
                var tasks = batch.Select(b => ExtractFeaturesAsync(b.imageData, b.roi, config, ct));
                var results = await Task.WhenAll(tasks);
                return results.ToList();
            }
        }

        private Mat CropAndResize(Mat source, BoundingBox roi, OSNetConfig config)
        {
            // Clamp ROI to image bounds
            var clampedRoi = roi.ClampTo(source.Width, source.Height);

            var rect = new Rect(clampedRoi.X, clampedRoi.Y, clampedRoi.Width, clampedRoi.Height);
            using var cropped = new Mat(source, rect);

            var resized = new Mat();
            Cv2.Resize(cropped, resized, new CvSize(config.InputWidth, config.InputHeight));

            return resized;
        }

        private DenseTensor<float> PrepareInput(Mat image, OSNetConfig config)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, config.InputHeight, config.InputWidth });

            using var rgbMat = new Mat();
            Cv2.CvtColor(image, rgbMat, ColorConversionCodes.BGR2RGB);

            var indexer = rgbMat.GetGenericIndexer<Vec3b>();

            for (int y = 0; y < config.InputHeight; y++)
            {
                for (int x = 0; x < config.InputWidth; x++)
                {
                    var pixel = indexer[y, x];

                    // Normalize: (pixel / 255 - mean) / std
                    tensor[0, 0, y, x] = ((pixel.Item0 / 255f) - config.Mean[0]) / config.Std[0]; // R
                    tensor[0, 1, y, x] = ((pixel.Item1 / 255f) - config.Mean[1]) / config.Std[1]; // G
                    tensor[0, 2, y, x] = ((pixel.Item2 / 255f) - config.Mean[2]) / config.Std[2]; // B
                }
            }

            return tensor;
        }

        private float[] RunInference(DenseTensor<float> input)
        {
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, input)
            };

            using var results = _session.Run(inputs);
            return results.First().AsEnumerable<float>().ToArray();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _session?.Dispose();
                _disposed = true;
            }
        }
    }
}