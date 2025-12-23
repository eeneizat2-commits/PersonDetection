namespace PersonDetection.Domain.Events
{
    using PersonDetection.Domain.Common;

    public record PersonsDetectedEvent(int CameraId, int Count, DateTime OccurredOn) : IDomainEvent;
    public record CameraStartedEvent(int CameraId, string Url, DateTime OccurredOn) : IDomainEvent;
    public record CameraStoppedEvent(int CameraId, DateTime OccurredOn) : IDomainEvent;
    public record PersonIdentifiedEvent(Guid PersonId, int CameraId, float Confidence, DateTime OccurredOn) : IDomainEvent;
}