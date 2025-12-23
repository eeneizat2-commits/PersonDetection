namespace PersonDetection.Domain.ValueObjects
{
    public record FeatureVector
    {
        private readonly float[] _values;

        public FeatureVector(float[] values)
        {
            if (values == null || values.Length == 0)
                throw new ArgumentException("Feature vector cannot be empty", nameof(values));

            _values = (float[])values.Clone();
        }

        public float[] Values => (float[])_values.Clone();
        public int Dimension => _values.Length;

        public float CosineSimilarity(FeatureVector other)
        {
            if (other.Dimension != Dimension)
                throw new ArgumentException("Vectors must have same dimension");

            float dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < Dimension; i++)
            {
                dot += _values[i] * other._values[i];
                normA += _values[i] * _values[i];
                normB += other._values[i] * other._values[i];
            }

            var magnitude = (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
            return magnitude == 0 ? 0 : dot / magnitude;
        }

        public float EuclideanDistance(FeatureVector other)
        {
            if (other.Dimension != Dimension)
                throw new ArgumentException("Vectors must have same dimension");

            float sum = 0;
            for (int i = 0; i < Dimension; i++)
            {
                var diff = _values[i] - other._values[i];
                sum += diff * diff;
            }
            return (float)Math.Sqrt(sum);
        }

        public FeatureVector Normalize()
        {
            float norm = (float)Math.Sqrt(_values.Sum(v => v * v));
            if (norm == 0) return this;

            var normalized = _values.Select(v => v / norm).ToArray();
            return new FeatureVector(normalized);
        }
    }
}