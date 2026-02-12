// PersonDetection.Application/DTOs/StatsDtos.cs
namespace PersonDetection.Application.DTOs
{
    public class HistoricalStatsDto
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalDays { get; set; }
        public int TotalUniquePersons { get; set; }
        public int TotalDetections { get; set; }
        public List<DailyStatsDto> DailyStats { get; set; } = new();
        public List<CameraBreakdownDto> CameraBreakdown { get; set; } = new();
    }

    public class DailyStatsDto
    {
        public DateTime Date { get; set; }
        public string DayName { get; set; } = string.Empty;
        public int UniquePersons { get; set; }
        public int TotalDetections { get; set; }
        public int PeakHour { get; set; }
        public int PeakHourCount { get; set; }
    }

    public class CameraBreakdownDto
    {
        public int CameraId { get; set; }
        public string CameraName { get; set; } = string.Empty;
        public int TotalDetections { get; set; }
        public int UniquePersons { get; set; }
    }

    public class SummaryStatsDto
    {
        public PeriodStatsDto Today { get; set; } = new();
        public PeriodStatsDto ThisWeek { get; set; } = new();
        public PeriodStatsDto ThisMonth { get; set; } = new();
    }

    public class PeriodStatsDto
    {
        public int UniquePersons { get; set; }
        public int Detections { get; set; }
        public int DailyAverage { get; set; }
    }
}