namespace PersonDetection.Infrastructure.ReId
{
    using Microsoft.ML.OnnxRuntime;
    using Microsoft.ML.OnnxRuntime.Tensors;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.ValueObjects;

    public abstract class GenericReIdEngine<TConfig> : IReIdentificationEngine<TConfig> where TConfig : class
    {
        protected readonly InferenceSession _session;
        protected readonly ILogger _logger;

        public abstract int VectorDimension { get; }

        protected GenericReIdEngine(string modelPath, bool useGpu, ILogger logger)
        {
            _logger = logger;

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };

            if (useGpu)
            {
                try
                {
                    options.AppendExecutionProvider_OpenVINO();
                    _logger.LogInformation("ReID: OpenVINO acceleration enabled");
                }
                catch
                {
                    _logger.LogWarning("ReID: Falling back to CPU");
                }
            }

            _session = new InferenceSession(modelPath, options);
        }

        public async Task<FeatureVector> ExtractFeaturesAsync(byte[] imageData, BoundingBox roi, TConfig config, CancellationToken ct)
        {
            var batch = new List<(byte[], BoundingBox)> { (imageData, roi) };
            var vectors = await ExtractFeaturesBatchAsync(batch, config, ct);
            return vectors[0];
        }

        public async Task<List<FeatureVector>> ExtractFeaturesBatchAsync(
            List<(byte[] imageData, BoundingBox roi)> batch,
            TConfig config,
            CancellationToken ct)
        {
            if (batch.Count == 0) return new List<FeatureVector>();

            var inputTensor = await PreprocessBatchAsync(batch, config, ct);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor)
            };

            using var results = _session.Run(inputs);
            var output = results.First().AsEnumerable<float>().ToArray();

            return ParseOutput(output, batch.Count);
        }

        protected abstract Task<DenseTensor<float>> PreprocessBatchAsync(
            List<(byte[] imageData, BoundingBox roi)> batch,
            TConfig config,
            CancellationToken ct);

        private List<FeatureVector> ParseOutput(float[] output, int batchSize)
        {
            var vectors = new List<FeatureVector>();
            var stride = VectorDimension;

            for (int i = 0; i < batchSize; i++)
            {
                var vector = new float[VectorDimension];
                Array.Copy(output, i * stride, vector, 0, VectorDimension);
                vectors.Add(new FeatureVector(vector));
            }

            return vectors;
        }
    }


}