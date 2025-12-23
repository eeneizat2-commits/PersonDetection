// PersonDetection.Infrastructure/Detection/YoloV11DetectionEngine.cs
namespace PersonDetection.Infrastructure.Detection
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.ML.OnnxRuntime;
    using Microsoft.ML.OnnxRuntime.Tensors;
    using OpenCvSharp;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.ValueObjects;

    public class YoloDetectionConfig
    {
        public float ConfidenceThreshold { get; set; } = 0.4f;
        public float NmsThreshold { get; set; } = 0.45f;
        public int ModelInputSize { get; set; } = 640;
        public int PersonClassId { get; set; } = 0;
    }

    public class YoloV11DetectionEngine : IDetectionEngine<YoloDetectionConfig>, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly ILogger<YoloV11DetectionEngine> _logger;
        private readonly string _inputName;
        private readonly int[] _inputShape;
        private bool _disposed;

        public string Name => "YOLOv11s";
        public bool IsGpuAccelerated { get; }

        public YoloV11DetectionEngine(string modelPath, bool useGpu, ILogger<YoloV11DetectionEngine> logger)
        {
            _logger = logger;

            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"YOLO model not found: {modelPath}");

            var options = CreateSessionOptions(useGpu, out var gpuEnabled);
            IsGpuAccelerated = gpuEnabled;

            _session = new InferenceSession(modelPath, options);

            var inputMeta = _session.InputMetadata.First();
            _inputName = inputMeta.Key;
            _inputShape = inputMeta.Value.Dimensions;

            _logger.LogInformation("{Name} initialized. GPU: {Gpu}, Input: {Shape}",
                Name, IsGpuAccelerated, string.Join("x", _inputShape));
        }

        private SessionOptions CreateSessionOptions(bool useGpu, out bool gpuEnabled)
        {
            gpuEnabled = false;
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_PARALLEL,
                IntraOpNumThreads = Environment.ProcessorCount
            };

            if (useGpu)
            {
                try
                {
                    options.AppendExecutionProvider_CUDA(0);
                    gpuEnabled = true;
                    _logger.LogInformation("{Name}: CUDA GPU acceleration enabled", Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Name}: GPU not available, using CPU", Name);
                }
            }

            return options;
        }

        public async Task<List<DetectedPerson>> DetectAsync(
            byte[] imageData,
            YoloDetectionConfig config,
            CancellationToken ct = default)
        {
            using var stream = new MemoryStream(imageData);
            return await DetectAsync(stream, config, ct);
        }

        public async Task<List<DetectedPerson>> DetectAsync(
            Stream imageStream,
            YoloDetectionConfig config,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                using var ms = new MemoryStream();
                imageStream.CopyTo(ms);
                var imageData = ms.ToArray();

                using var originalMat = Mat.FromImageData(imageData, ImreadModes.Color);
                var originalWidth = originalMat.Width;
                var originalHeight = originalMat.Height;

                // Preprocess
                var (tensor, scaleX, scaleY, padX, padY) = PreprocessImage(originalMat, config);

                // Run inference
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, tensor)
                };

                using var results = _session.Run(inputs);
                var output = results.First().AsTensor<float>();

                // Postprocess
                var detections = PostprocessOutput(output, config, originalWidth, originalHeight, scaleX, scaleY, padX, padY);

                // Apply NMS
                return ApplyNMS(detections, config.NmsThreshold);
            }, ct);
        }

        private (DenseTensor<float> tensor, float scaleX, float scaleY, int padX, int padY) PreprocessImage(
            Mat image,
            YoloDetectionConfig config)
        {
            var inputSize = config.ModelInputSize;

            // Calculate scale and padding (letterbox)
            var scale = Math.Min((float)inputSize / image.Width, (float)inputSize / image.Height);
            var newWidth = (int)(image.Width * scale);
            var newHeight = (int)(image.Height * scale);
            var padX = (inputSize - newWidth) / 2;
            var padY = (inputSize - newHeight) / 2;

            // Resize
            using var resized = new Mat();
            Cv2.Resize(image, resized, new Size(newWidth, newHeight));

            // Create padded image
            using var padded = new Mat(inputSize, inputSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
            var roi = new Rect(padX, padY, newWidth, newHeight);
            resized.CopyTo(new Mat(padded, roi));

            // Convert to RGB and normalize
            using var rgb = new Mat();
            Cv2.CvtColor(padded, rgb, ColorConversionCodes.BGR2RGB);

            // Create tensor
            var tensor = new DenseTensor<float>(new[] { 1, 3, inputSize, inputSize });
            var indexer = rgb.GetGenericIndexer<Vec3b>();

            for (int y = 0; y < inputSize; y++)
            {
                for (int x = 0; x < inputSize; x++)
                {
                    var pixel = indexer[y, x];
                    tensor[0, 0, y, x] = pixel.Item0 / 255f; // R
                    tensor[0, 1, y, x] = pixel.Item1 / 255f; // G
                    tensor[0, 2, y, x] = pixel.Item2 / 255f; // B
                }
            }

            return (tensor, scale, scale, padX, padY);
        }

        private List<DetectedPerson> PostprocessOutput(
            Tensor<float> output,
            YoloDetectionConfig config,
            int originalWidth,
            int originalHeight,
            float scaleX,
            float scaleY,
            int padX,
            int padY)
        {
            var detections = new List<DetectedPerson>();

            // YOLOv11 output shape: [1, 84, 8400] - need to transpose to [1, 8400, 84]
            var dims = output.Dimensions.ToArray();
            var numClasses = dims[1] - 4; // 80 classes for COCO
            var numDetections = dims[2];

            for (int i = 0; i < numDetections; i++)
            {
                // Find best class
                float maxScore = 0;
                int bestClass = 0;

                for (int c = 0; c < numClasses; c++)
                {
                    var score = output[0, 4 + c, i];
                    if (score > maxScore)
                    {
                        maxScore = score;
                        bestClass = c;
                    }
                }

                // Only keep person class (0) with sufficient confidence
                if (bestClass != config.PersonClassId || maxScore < config.ConfidenceThreshold)
                    continue;

                // Get bounding box (center x, center y, width, height)
                var cx = output[0, 0, i];
                var cy = output[0, 1, i];
                var w = output[0, 2, i];
                var h = output[0, 3, i];

                // Convert to corner coordinates and remove padding
                var x1 = (cx - w / 2 - padX) / scaleX;
                var y1 = (cy - h / 2 - padY) / scaleY;
                var x2 = (cx + w / 2 - padX) / scaleX;
                var y2 = (cy + h / 2 - padY) / scaleY;

                // Clamp to image bounds
                x1 = Math.Max(0, Math.Min(x1, originalWidth - 1));
                y1 = Math.Max(0, Math.Min(y1, originalHeight - 1));
                x2 = Math.Max(0, Math.Min(x2, originalWidth));
                y2 = Math.Max(0, Math.Min(y2, originalHeight));

                var width = (int)(x2 - x1);
                var height = (int)(y2 - y1);

                if (width <= 0 || height <= 0)
                    continue;

                var bbox = new BoundingBox((int)x1, (int)y1, width, height);
                detections.Add(DetectedPerson.Create(bbox, maxScore));
            }

            return detections;
        }

        private List<DetectedPerson> ApplyNMS(List<DetectedPerson> detections, float threshold)
        {
            if (detections.Count == 0) return detections;

            var sorted = detections.OrderByDescending(d => d.Confidence).ToList();
            var selected = new List<DetectedPerson>();
            var suppressed = new bool[sorted.Count];

            for (int i = 0; i < sorted.Count; i++)
            {
                if (suppressed[i]) continue;

                selected.Add(sorted[i]);

                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (suppressed[j]) continue;

                    var iou = sorted[i].BoundingBox.IoU(sorted[j].BoundingBox);
                    if (iou > threshold)
                    {
                        suppressed[j] = true;
                    }
                }
            }

            return selected;
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