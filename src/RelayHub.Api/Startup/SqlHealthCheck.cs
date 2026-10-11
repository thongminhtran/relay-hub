using Microsoft.Extensions.Diagnostics.HealthChecks;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Api.Startup;

public sealed class SqlHealthCheck(RelayHubDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy("database reachable")
                : HealthCheckResult.Unhealthy("database not reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database error", ex);
        }
    }
}
