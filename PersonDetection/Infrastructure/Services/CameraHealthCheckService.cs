// PersonDetection.Infrastructure/Services/CameraHealthCheckService.cs
namespace PersonDetection.Infrastructure.Services
{
    using Microsoft.AspNetCore.SignalR;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using PersonDetection.API.Hubs;
    using PersonDetection.Application.Commands;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Application.DTOs;
    using PersonDetection.Domain.Repositories;
    using PersonDetection.Infrastructure.Streaming;
    using System.Collections.Concurrent;

    public class CameraHealthCheckService : BackgroundService
    {
        private readonly IStreamProcessorFactory _processorFactory;
        private readonly IHubContext<DetectionHub> _hubContext;
        private readonly IServiceProvider _serviceProvider;
        private readonly IOptionsMonitor<HealthCheckSettings> _settingsMonitor;
        private readonly ILogger<CameraHealthCheckService> _logger;

        // Track when each camera entered error state
        private readonly ConcurrentDictionary<int, DateTime> _errorStartTimes = new();

        // Track cameras that were manually stopped (should not be auto-reconnected)
        private readonly ConcurrentDictionary<int, bool> _manuallyStoppedCameras = new();

        public CameraHealthCheckService(
            IStreamProcessorFactory processorFactory,
            IHubContext<DetectionHub> hubContext,
            IServiceProvider serviceProvider,
            IOptionsMonitor<HealthCheckSettings> settingsMonitor,
            ILogger<CameraHealthCheckService> logger)
        {
            _processorFactory = processorFactory;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;
            _settingsMonitor = settingsMonitor;
            _logger = logger;
        }

        /// <summary>
        /// Mark a camera as manually stopped so the health check won't try to reconnect it
        /// </summary>
        public void MarkAsManuallyStopped(int cameraId)
        {
            _manuallyStoppedCameras[cameraId] = true;
            _errorStartTimes.TryRemove(cameraId, out _);
            _logger.LogDebug("Camera {CameraId} marked as manually stopped", cameraId);
        }

        /// <summary>
        /// Clear manual stop flag (e.g., when user starts camera again)
        /// </summary>
        public void ClearManuallyStopped(int cameraId)
        {
            _manuallyStoppedCameras.TryRemove(cameraId, out _);
            _errorStartTimes.TryRemove(cameraId, out _);
            _logger.LogDebug("Camera {CameraId} manual stop flag cleared", cameraId);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var settings = _settingsMonitor.CurrentValue;

            if (!settings.Enabled)
            {
                _logger.LogInformation("🏥 Camera Health Check Service is DISABLED");
                return;
            }

            _logger.LogInformation(
                "🏥 Camera Health Check Service started — " +
                "Interval: {Interval}min, MaxRetries: {Retries}, InitialDelay: {Delay}s",
                settings.CheckIntervalMinutes,
                settings.MaxRetryAttemptsPerCycle,
                settings.InitialDelaySeconds);

            // Wait before first check to allow initial connections to complete
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(settings.InitialDelaySeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Re-read settings each cycle (supports hot-reload)
                    var currentSettings = _settingsMonitor.CurrentValue;

                    if (currentSettings.Enabled)
                    {
                        await RunHealthCheckCycleAsync(currentSettings, stoppingToken);
                    }

                    // Wait for next cycle
                    await Task.Delay(
                        TimeSpan.FromMinutes(currentSettings.CheckIntervalMinutes),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "🏥 Health check cycle failed unexpectedly");

                    // Wait a bit before retrying to avoid tight error loops
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("🏥 Camera Health Check Service stopped");
        }

        private async Task RunHealthCheckCycleAsync(
            HealthCheckSettings settings,
            CancellationToken ct)
        {
            var candidateCameras = await GetCandidateCamerasAsync(settings, ct);

            if (candidateCameras.Count == 0)
            {
                if (settings.LogLevel == "Verbose")
                {
                    _logger.LogDebug("🏥 Health check: No cameras need reconnection");
                }
                return;
            }

            _logger.LogInformation(
                "🏥 Health check cycle: Found {Count} camera(s) to check: [{Ids}]",
                candidateCameras.Count,
                string.Join(", ", candidateCameras.Select(c => c.CameraId)));

            var reconnected = 0;
            var failed = 0;

            foreach (var candidate in candidateCameras)
            {
                if (ct.IsCancellationRequested) break;

                var success = await TryReconnectCameraAsync(
                    candidate.CameraId,
                    candidate.Url,
                    settings,
                    ct);

                if (success)
                {
                    reconnected++;
                    _errorStartTimes.TryRemove(candidate.CameraId, out _);
                }
                else
                {
                    failed++;
                    // Track when this camera first entered error state
                    _errorStartTimes.TryAdd(candidate.CameraId, DateTime.UtcNow);
                }
            }

            _logger.LogInformation(
                "🏥 Health check cycle complete: {Reconnected} reconnected, {Failed} failed",
                reconnected, failed);
        }

        private async Task<List<CameraCandidate>> GetCandidateCamerasAsync(
            HealthCheckSettings settings,
            CancellationToken ct)
        {
            var candidates = new List<CameraCandidate>();

            // Get all processors from the factory
            var allProcessors = _processorFactory.GetAll();

            // Get enabled cameras from database if needed
            Dictionary<int, string>? enabledCameraUrls = null;

            if (settings.CheckOnlyEnabledCameras)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var cameraRepo = scope.ServiceProvider
                        .GetRequiredService<ICameraConfigRepository>();

                    var enabledCameras = await cameraRepo.GetEnabledAsync(ct);
                    enabledCameraUrls = enabledCameras
                        .ToDictionary(c => c.Id, c => c.Url);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "🏥 Failed to load enabled cameras from DB, checking all processors");
                }
            }

            foreach (var (cameraId, processor) in allProcessors)
            {
                // Skip manually stopped cameras
                if (_manuallyStoppedCameras.ContainsKey(cameraId))
                {
                    if (settings.LogLevel == "Verbose")
                    {
                        _logger.LogDebug(
                            "🏥 Camera {CameraId}: Skipped (manually stopped)", cameraId);
                    }
                    continue;
                }

                // Skip if not in target states
                var state = processor.ConnectionState;
                var shouldCheck = false;

                if (settings.CheckErrorState &&
                    state == StreamConnectionState.Error)
                {
                    shouldCheck = true;
                }

                if (settings.CheckStoppedState &&
                    state == StreamConnectionState.Stopped)
                {
                    shouldCheck = true;
                }

                if (!shouldCheck) continue;

                // Skip if only checking enabled cameras and this one isn't enabled
                if (enabledCameraUrls != null &&
                    !enabledCameraUrls.ContainsKey(cameraId))
                {
                    if (settings.LogLevel == "Verbose")
                    {
                        _logger.LogDebug(
                            "🏥 Camera {CameraId}: Skipped (disabled in DB)", cameraId);
                    }
                    continue;
                }

                // Skip if exceeded max error duration
                if (settings.MaxErrorDurationMinutes > 0 &&
                    _errorStartTimes.TryGetValue(cameraId, out var errorStart))
                {
                    var errorDuration = DateTime.UtcNow - errorStart;
                    if (errorDuration.TotalMinutes > settings.MaxErrorDurationMinutes)
                    {
                        _logger.LogWarning(
                            "🏥 Camera {CameraId}: Skipped (in error state for {Duration:F0} min, max={Max} min)",
                            cameraId,
                            errorDuration.TotalMinutes,
                            settings.MaxErrorDurationMinutes);
                        continue;
                    }
                }

                // Determine URL: prefer DB URL, fall back to processor's stored URL
                var url = enabledCameraUrls?.GetValueOrDefault(cameraId)
                          ?? processor.StreamUrl;

                if (string.IsNullOrEmpty(url))
                {
                    _logger.LogWarning(
                        "🏥 Camera {CameraId}: Skipped (no URL available)", cameraId);
                    continue;
                }

                candidates.Add(new CameraCandidate
                {
                    CameraId = cameraId,
                    Url = url,
                    State = state
                });
            }

            // Also check enabled cameras that don't have processors yet
            // (e.g., cameras that were enabled but never started, or processor was removed)
            if (enabledCameraUrls != null)
            {
                foreach (var (cameraId, url) in enabledCameraUrls)
                {
                    if (allProcessors.ContainsKey(cameraId)) continue;
                    if (_manuallyStoppedCameras.ContainsKey(cameraId)) continue;

                    // Check if there's an active session for this camera
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                        var session = await uow.Cameras.GetActiveSessionAsync(cameraId, ct);

                        if (session != null && session.IsActive)
                        {
                            _logger.LogInformation(
                                "🏥 Camera {CameraId}: Has active session but no processor — will reconnect",
                                cameraId);

                            candidates.Add(new CameraCandidate
                            {
                                CameraId = cameraId,
                                Url = url,
                                State = StreamConnectionState.Error,
                                NeedsNewProcessor = true
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "🏥 Failed to check session for camera {CameraId}", cameraId);
                    }
                }
            }

            return candidates;
        }

        private async Task<bool> TryReconnectCameraAsync(
            int cameraId,
            string url,
            HealthCheckSettings settings,
            CancellationToken ct)
        {
            _logger.LogInformation(
                "🏥 Camera {CameraId}: Starting reconnection attempts (max {Max})",
                cameraId, settings.MaxRetryAttemptsPerCycle);

            // Notify UI that health check is attempting reconnection
            if (settings.NotifyOnReconnection)
            {
                await NotifyHealthCheckStatusAsync(cameraId,
                    "health_check_started",
                    "Health check attempting reconnection",
                    ct);
            }

            for (int attempt = 1; attempt <= settings.MaxRetryAttemptsPerCycle; attempt++)
            {
                if (ct.IsCancellationRequested) return false;

                try
                {
                    _logger.LogInformation(
                        "🏥 Camera {CameraId}: Reconnection attempt {Attempt}/{Max}",
                        cameraId, attempt, settings.MaxRetryAttemptsPerCycle);

                    // Get or create processor
                    var processor = _processorFactory.Get(cameraId);

                    if (processor == null)
                    {
                        _logger.LogInformation(
                            "🏥 Camera {CameraId}: Creating new processor", cameraId);
                        processor = _processorFactory.Create(cameraId, url);
                    }

                    // Attempt connection with timeout
                    using var timeoutCts = CancellationTokenSource
                        .CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(
                        TimeSpan.FromSeconds(settings.ConnectionTimeoutSeconds));

                    var connected = await processor.ConnectAsync(url, timeoutCts.Token);

                    if (connected)
                    {
                        _logger.LogInformation(
                            "✅ Camera {CameraId}: Reconnected successfully on attempt {Attempt}",
                            cameraId, attempt);

                        // Update database
                        if (settings.UpdateLastConnectedOnReconnect)
                        {
                            await UpdateLastConnectedAsync(cameraId, ct);
                        }

                        // Notify UI
                        if (settings.NotifyOnReconnection)
                        {
                            await NotifyHealthCheckStatusAsync(cameraId,
                                "health_check_reconnected",
                                "Camera reconnected by health check",
                                ct);
                        }

                        return true;
                    }

                    _logger.LogWarning(
                        "🏥 Camera {CameraId}: Attempt {Attempt} failed (ConnectAsync returned false)",
                        cameraId, attempt);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "🏥 Camera {CameraId}: Attempt {Attempt} timed out after {Timeout}s",
                        cameraId, attempt, settings.ConnectionTimeoutSeconds);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "🏥 Camera {CameraId}: Attempt {Attempt} failed with exception",
                        cameraId, attempt);
                }

                // Wait before next retry (unless it's the last attempt)
                if (attempt < settings.MaxRetryAttemptsPerCycle)
                {
                    try
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(settings.RetryDelaySeconds), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }

            _logger.LogWarning(
                "❌ Camera {CameraId}: All {Max} reconnection attempts failed",
                cameraId, settings.MaxRetryAttemptsPerCycle);

            // Notify UI of failure
            if (settings.NotifyOnFailure)
            {
                await NotifyHealthCheckStatusAsync(cameraId,
                    "health_check_failed",
                    $"Health check failed after {settings.MaxRetryAttemptsPerCycle} attempts",
                    ct);
            }

            return false;
        }

        private async Task UpdateLastConnectedAsync(int cameraId, CancellationToken ct)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var cameraRepo = scope.ServiceProvider
                    .GetRequiredService<ICameraConfigRepository>();

                var camera = await cameraRepo.GetByIdAsync(cameraId, ct);
                if (camera != null)
                {
                    camera.UpdateLastConnected();
                    await cameraRepo.UpdateAsync(camera, ct);

                    _logger.LogDebug(
                        "🏥 Camera {CameraId}: LastConnectedAt updated", cameraId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "🏥 Failed to update LastConnectedAt for camera {CameraId}",
                    cameraId);
            }
        }

        private async Task NotifyHealthCheckStatusAsync(
            int cameraId,
            string eventType,
            string message,
            CancellationToken ct)
        {
            try
            {
                var notification = new
                {
                    cameraId,
                    eventType,
                    message,
                    timestamp = DateTime.UtcNow
                };

                // Notify camera-specific group
                await _hubContext.Clients
                    .Group($"camera_{cameraId}")
                    .SendAsync("HealthCheckUpdate", notification, ct);

                // Notify global health check subscribers
                await _hubContext.Clients
                    .Group("health_check")
                    .SendAsync("HealthCheckUpdate", notification, ct);

                // Also notify stream status group (so existing UI code picks it up)
                await _hubContext.Clients
                    .Group("stream_status")
                    .SendAsync("HealthCheckUpdate", notification, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "🏥 Failed to send health check notification for camera {CameraId}",
                    cameraId);
            }
        }

        private class CameraCandidate
        {
            public int CameraId { get; set; }
            public string Url { get; set; } = string.Empty;
            public StreamConnectionState State { get; set; }
            public bool NeedsNewProcessor { get; set; }
        }
    }
}