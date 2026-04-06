// Domain/Entities/UniquePersonFeature.cs
namespace PersonDetection.Domain.Entities
{
    public class UniquePersonFeature
    {
        public int UniquePersonId { get; set; }
        public string? FeatureVector { get; set; }
        public UniquePerson? UniquePerson { get; set; }
    }
}