// PersonDetection.Infrastructure/Services/IdentityCleanupService.cs
namespace PersonDetection.Infrastructure.Services
{
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Domain.Services;

    public class IdentityCleanupService : BackgroundService
    {
        private readonly IPersonIdentityMatcher _identityMatcher;
        private readonly IdentitySettings _settings;
        private readonly ILogger<IdentityCleanupService> _logger;

        public IdentityCleanupService(
            IPersonIdentityMatcher identityMatcher,
            IOptions<IdentitySettings> settings,
            ILogger<IdentityCleanupService> logger)
        {
            _identityMatcher = identityMatcher;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Identity cleanup service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

                    var expiration = TimeSpan.FromMinutes(_settings.CacheExpirationMinutes);
                    _identityMatcher.CleanupExpired(expiration);

                    _logger.LogDebug("Identity cleanup completed. Active: {Count}",
                        _identityMatcher.GetActiveIdentityCount());
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in identity cleanup service");
                }
            }

            _logger.LogInformation("Identity cleanup service stopped");
        }
    }
}