// PersonDetection.Application/Commands/ProcessFrameCommand.cs
namespace PersonDetection.Application.Commands
{
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using PersonDetection.Application.Common;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Application.Interfaces;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Repositories;
    using PersonDetection.Domain.Services;

    public record ProcessFrameCommand(int CameraId, byte[] FrameData, bool ForceSave = false) : ICommand<DetectionResultDto>;

    public class ProcessFrameHandler<TDetectionConfig, TReIdConfig>
        : ICommandHandler<ProcessFrameCommand, DetectionResultDto>
        where TDetectionConfig : class, new()
        where TReIdConfig : class, new()
    {
        private readonly IDetectionEngine<TDetectionConfig> _detectionEngine;
        private readonly IReIdentificationEngine<TReIdConfig> _reidEngine;
        private readonly IPersonIdentityMatcher _identityMatcher;
        private readonly IUnitOfWork _uow;
        private readonly PersistenceSettings _persistenceSettings;
        private readonly ILogger<ProcessFrameHandler<TDetectionConfig, TReIdConfig>> _logger;

        // In-memory state for throttling DB saves
        private static readonly Dictionary<int, (DateTime LastSave, int LastCount)> _saveState = new();
        private static readonly object _stateLock = new();

        public ProcessFrameHandler(
            IDetectionEngine<TDetectionConfig> detectionEngine,
            IReIdentificationEngine<TReIdConfig> reidEngine,
            IPersonIdentityMatcher identityMatcher,
            IUnitOfWork uow,
            IOptions<PersistenceSettings> persistenceSettings,
            ILogger<ProcessFrameHandler<TDetectionConfig, TReIdConfig>> logger)
        {
            _detectionEngine = detectionEngine;
            _reidEngine = reidEngine;
            _identityMatcher = identityMatcher;
            _uow = uow;
            _persistenceSettings = persistenceSettings.Value;
            _logger = logger;
        }

        public async Task<DetectionResultDto> Handle(ProcessFrameCommand cmd, CancellationToken ct)
        {
            // 1. Detect persons
            var config = new TDetectionConfig();
            var detections = await _detectionEngine.DetectAsync(cmd.FrameData, config, ct);

            // 2. Create result from detections
            var result = DetectionResult.Create(cmd.CameraId, detections);

            // 3. Check if we should save to DB
            bool shouldSave = ShouldSaveToDatabase(cmd.CameraId, result.ValidDetections, cmd.ForceSave);

            if (shouldSave && _persistenceSettings.SaveToDatabase)
            {
                // Only extract features and save when persisting
                if (detections.Count > 0)
                {
                    var batch = detections.Select(d => (cmd.FrameData, d.BoundingBox)).ToList();
                    var reidConfig = new TReIdConfig();

                    try
                    {
                        var features = await _reidEngine.ExtractFeaturesBatchAsync(batch, reidConfig, ct);

                        for (int i = 0; i < detections.Count; i++)
                        {
                            // ✅ FIX: Pass features[i] directly - AssignIdentity now accepts FeatureVector
                            var personId = _identityMatcher.GetOrCreateIdentity(features[i]);
                            detections[i].AssignIdentity(personId, features[i]);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ReID failed, saving without identity");
                    }
                }

                await _uow.Detections.SaveAsync(result, ct);
                await _uow.SaveChangesAsync(ct);

                _logger.LogDebug("Saved detection result for camera {CameraId}: {Count} persons",
                    cmd.CameraId, result.ValidDetections);
            }

            return MapToDto(result);
        }

        private bool ShouldSaveToDatabase(int cameraId, int currentCount, bool forceSave)
        {
            if (forceSave) return true;
            if (!_persistenceSettings.SaveToDatabase) return false;

            lock (_stateLock)
            {
                var now = DateTime.UtcNow;

                if (!_saveState.TryGetValue(cameraId, out var state))
                {
                    _saveState[cameraId] = (now, currentCount);
                    return true;
                }

                var timeSinceLastSave = (now - state.LastSave).TotalSeconds;
                var countChanged = Math.Abs(currentCount - state.LastCount) >= _persistenceSettings.MinCountChangeThreshold;

                if (timeSinceLastSave >= _persistenceSettings.SaveIntervalSeconds)
                {
                    if (!_persistenceSettings.OnlyOnCountChange || countChanged)
                    {
                        _saveState[cameraId] = (now, currentCount);
                        return true;
                    }
                }

                return false;
            }
        }

        private DetectionResultDto MapToDto(DetectionResult result)
        {
            var persons = result.Detections.Select(d => new PersonDetectionDto(
                d.Id,
                new BoundingBoxDto(d.BoundingBox_X, d.BoundingBox_Y, d.BoundingBox_Width, d.BoundingBox_Height,
                    d.BoundingBox_Width > 0 ? (float)d.BoundingBox_Height / d.BoundingBox_Width : 0),
                d.Confidence,
                d.GlobalPersonId,
                d.TrackId,
                d.DetectedAt
            )).ToList();

            return new DetectionResultDto(
                result.Id,
                result.CameraId,
                result.Timestamp,
                result.TotalDetections,
                result.ValidDetections,
                persons
            );
        }
    }
}