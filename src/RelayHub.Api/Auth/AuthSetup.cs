using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using RelayHub.Api.Startup;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure;

namespace RelayHub.Api.Auth;

public static class RateLimitPolicies
{
    public const string Login = "auth-login";
    public const string Demo = "auth-demo";
}

public static class AuthSetup
{
    /// <summary>JWT bearer authentication, the Owner / Member policies, per-IP rate limits for login/demo, CORS for the dashboard, password hashing.</summary>
    public static IServiceCollection AddRelayHubAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<JwtTokens>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddHttpClient<TurnstileVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<JwtTokens>((options, jwt) =>
        {
            options.TokenValidationParameters = jwt.ValidationParameters;
            options.MapInboundClaims = false; // keep "sub" / "role" / "ws" as they are in the token
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Owner, p => p.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Owner)))
            .AddPolicy(AuthPolicies.Member, p => p.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Owner), nameof(UserRole.Demo)));

        // Fixed window per client IP: login 10/min, demo 5/min. 429 bodies become Problem Details through UseStatusCodePages.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicies.Login, context => PerIp(context, permitLimit: 10));
            options.AddPolicy(RateLimitPolicies.Demo, context => PerIp(context, permitLimit: 5));
        });

        var origins = (configuration[$"{RelayHubOptions.SectionName}:{nameof(RelayHubOptions.DashboardOrigin)}"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (environment.IsDevelopment()) origins.Add("http://localhost:5173");
        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins([.. origins.Distinct()])
            .WithHeaders("Authorization", "Content-Type")
            .WithMethods("GET", "POST", "PUT", "DELETE")
            .WithExposedHeaders("X-Correlation-Id")));

        return services;
    }

    /// <summary>Sets the ambient workspace from the <c>ws</c> claim. Unauthenticated requests have none, so the query filters match nothing.</summary>
    public static IApplicationBuilder UseWorkspaceFromToken(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            context.RequestServices.GetRequiredService<AmbientWorkspace>().WorkspaceId = context.User.WorkspaceId();
            await next();
        });

    private static RateLimitPartition<string> PerIp(HttpContext context, int permitLimit)
        => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
