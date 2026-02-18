namespace PersonDetection.Application.DTOs
{
    public enum StreamConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Reconnecting = 3,
        Error = 4,
        Stopped = 5
    }

    public record StreamStatusDto
    {
        public int CameraId { get; init; }
        public StreamConnectionState State { get; init; }
        public string StateMessage { get; init; } = string.Empty;
        public int ReconnectAttempt { get; init; }
        public int MaxReconnectAttempts { get; init; }
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public string? ErrorMessage { get; init; }
        public double? Fps { get; init; }
        public int ConsecutiveErrors { get; init; }
    }
}