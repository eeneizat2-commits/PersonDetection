namespace PersonDetection.Application.Configuration
{
    public class PersistenceSettings
    {
        public const string SectionName = "PersistenceConfig";

        public bool SaveToDatabase { get; set; } = true;
        public int SaveIntervalSeconds { get; set; } = 10;
        public bool OnlyOnCountChange { get; set; } = true;
        public int MinCountChangeThreshold { get; set; } = 1;
        public int MaxStoredResults { get; set; } = 1000;
        public int CleanupIntervalMinutes { get; set; } = 60;

        // ═══════════════════════════════════════════════════════════════
        // NEW: Add these if missing
        // ═══════════════════════════════════════════════════════════════
        public bool BatchSaveEnabled { get; set; } = true;
        public int BatchSize { get; set; } = 10;
    }
}
