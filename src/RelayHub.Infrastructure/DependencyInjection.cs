using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayHub.Core.Abstractions;
using RelayHub.Infrastructure.Connectors;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;

namespace RelayHub.Infrastructure;

public static class DependencyInjection
{
    private const string FallbackConnectionString =
        "Server=localhost,1433;Database=RelayHub;User Id=sa;Password=CHANGE_ME;TrustServerCertificate=True";

    public static IServiceCollection AddRelayHubInfrastructure(this IServiceCollection services, string? connectionString)
    {
        services.AddScoped<AmbientWorkspace>();
        services.AddScoped<ICurrentWorkspace>(sp => sp.GetRequiredService<AmbientWorkspace>());

        services.AddDbContext<RelayHubDbContext>(options =>
            options.UseSqlServer(
                string.IsNullOrWhiteSpace(connectionString) ? FallbackConnectionString : connectionString,
                // Serverless Azure SQL may be resuming from auto-pause: retry transient failures instead of failing fast.
                sql => sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)));

        services.AddHttpClient<DiscordConnector>(client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IDestinationConnector>(sp => sp.GetRequiredService<DiscordConnector>());
        services.AddScoped<DeliveryDispatcher>();

        return services;
    }
}

/// <summary>Per-request workspace scope. Set by the API (from auth) or by the worker (from the queue message).</summary>
public sealed class AmbientWorkspace : ICurrentWorkspace
{
    public int? WorkspaceId { get; set; }
}
