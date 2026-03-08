namespace PersonDetection.Application.DTOs
{
    public  class BatchPersonDto
    {
        public int Id { get; set; }
        public Guid GlobalPersonId { get; set; }
        public string? FeatureVector { get; set; }
        public DateTime FirstSeenAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public int FirstSeenCameraId { get; set; }
        public int LastSeenCameraId { get; set; }
        public int TotalSightings { get; set; }
    }
}
