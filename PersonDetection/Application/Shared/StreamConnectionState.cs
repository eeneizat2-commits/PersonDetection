namespace PersonDetection.Application.Shared
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
}
