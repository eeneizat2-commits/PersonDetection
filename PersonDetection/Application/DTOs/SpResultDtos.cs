namespace PersonDetection.Application.DTOs
{
    /// <summary>
    /// Result from sp_GetStatsSummary
    /// </summary>
    public class SpStatsSummary
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalDays { get; set; }
        public int TotalUniquePersons { get; set; }
        public int TotalDetections { get; set; }
    }

    /// <summary>
    /// Result from sp_GetDailyStats
    /// </summary>
    public class SpDailyStats
    {
        public DateTime Date { get; set; }
        public string DayName { get; set; } = string.Empty;
        public int UniquePersons { get; set; }
        public int TotalDetections { get; set; }
        public int PeakHour { get; set; }
        public int PeakHourCount { get; set; }
    }

    /// <summary>
    /// Result from sp_GetCameraBreakdown
    /// </summary>
    public class SpCameraBreakdown
    {
        public int CameraId { get; set; }
        public string CameraName { get; set; } = string.Empty;
        public int TotalDetections { get; set; }
        public int UniquePersons { get; set; }
    }

    /// <summary>
    /// Result from sp_GetHourlyStats
    /// </summary>
    public class SpHourlyStats
    {
        public int Hour { get; set; }
        public int TotalDetections { get; set; }
        public int UniquePersons { get; set; }
    }
}