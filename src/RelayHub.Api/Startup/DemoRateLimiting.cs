using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace RelayHub.Api.Startup;

/// <summary>
/// Rate-limit policies of the demo endpoints. Kept apart from <c>Program.cs</c> so other features can add their own policies
/// in the same <c>AddRateLimiter</c> call with one line (<c>DemoRateLimiting.Add(options)</c>).
/// </summary>
public static class DemoRateLimiting
{
    /// <summary>POST /api/v1/demo/simulate: 20 per minute per client IP (plan 9.3: visitors must not burn the free quotas).</summary>
    public const string SimulatePolicy = "demo-simulate";

    public static void Add(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(SimulatePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }
}
