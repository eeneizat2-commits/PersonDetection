// DatabaseHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PersonDetection.Infrastructure.Context;

public class DatabaseHealthCheck : IHealthCheck
{
    private readonly DetectionContext _context;

    public DatabaseHealthCheck(DetectionContext context)
    {
        _context = context;  // ✅ injected from the REAL container
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        try
        {
            return await _context.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy("Database connection OK")
                : HealthCheckResult.Unhealthy("Cannot connect to database");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Database error: {ex.Message}");
        }
    }
}