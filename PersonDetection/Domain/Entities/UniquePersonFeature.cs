namespace PersonDetection.Domain.Entities
{
    public class UniquePersonFeature
    {
        public int Id { get; set; }
        public Guid GlobalPersonId { get; set; }
        public string? FeatureVector { get; set; }
        public UniquePerson? UniquePerson { get; set; }
    }
}