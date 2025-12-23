// PersonDetection.Domain/Specifications/DetectionSpecifications.cs
namespace PersonDetection.Domain.Specifications
{
    using PersonDetection.Domain.Entities;

    public interface ISpecification<T>
    {
        bool IsSatisfiedBy(T entity);
    }

    public class MinQualitySpecification : ISpecification<DetectedPerson>
    {
        private readonly float _minConfidence;
        private readonly int _minWidth;
        private readonly int _minHeight;
        private readonly float _minAspectRatio;

        public MinQualitySpecification(float minConfidence, int minWidth, int minHeight, float minAspectRatio)
        {
            _minConfidence = minConfidence;
            _minWidth = minWidth;
            _minHeight = minHeight;
            _minAspectRatio = minAspectRatio;
        }

        public bool IsSatisfiedBy(DetectedPerson entity)
        {
            // ✅ Now this works because DetectedPerson has MeetsMinimumQuality method
            return entity.MeetsMinimumQuality(_minConfidence, _minWidth, _minHeight, _minAspectRatio);
        }
    }

    public class AndSpecification<T> : ISpecification<T>
    {
        private readonly ISpecification<T> _left;
        private readonly ISpecification<T> _right;

        public AndSpecification(ISpecification<T> left, ISpecification<T> right)
        {
            _left = left;
            _right = right;
        }

        public bool IsSatisfiedBy(T entity)
        {
            return _left.IsSatisfiedBy(entity) && _right.IsSatisfiedBy(entity);
        }
    }

    public class OrSpecification<T> : ISpecification<T>
    {
        private readonly ISpecification<T> _left;
        private readonly ISpecification<T> _right;

        public OrSpecification(ISpecification<T> left, ISpecification<T> right)
        {
            _left = left;
            _right = right;
        }

        public bool IsSatisfiedBy(T entity)
        {
            return _left.IsSatisfiedBy(entity) || _right.IsSatisfiedBy(entity);
        }
    }

    public class HighConfidenceSpecification : ISpecification<DetectedPerson>
    {
        private readonly float _threshold;

        public HighConfidenceSpecification(float threshold = 0.7f)
        {
            _threshold = threshold;
        }

        public bool IsSatisfiedBy(DetectedPerson entity)
        {
            return entity.Confidence >= _threshold;
        }
    }
}