using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Alerts;
using RelayHub.Infrastructure.Connectors;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;
using RelayHub.Infrastructure.Messaging;
using RelayHub.Infrastructure.Processing;

namespace RelayHub.Infrastructure;

public static class DependencyInjection
{
    private const string FallbackConnectionString =
        "Server=localhost,1433;Database=RelayHub;User Id=sa;Password=CHANGE_ME;TrustServerCertificate=True";

    /// <summary>Database, connectors, dispatcher and the worker-side processors. Shared by the API and the Functions worker.</summary>
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
        // Demo workspaces deliver here instead of a real connector (not an IDestinationConnector on purpose: never picked by type).
        services.AddSingleton<DemoSinkConnector>();
        // Live dashboard updates are optional; the SignalR implementation replaces this when configured (TryAdd keeps the override).
        services.TryAddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();
        services.AddScoped<DeliveryDispatcher>();
        services.AddScoped<IncomingProcessor>();
        services.AddScoped<DeliveryProcessor>();

        return services;
    }

    /// <summary>Storage Queues (section "Storage") and operator alerts ("RelayHub:OpsDiscordWebhookUrl").</summary>
    public static IServiceCollection AddRelayHubMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.AddSingleton<IRelayQueues, StorageQueues>();

        services.Configure<OpsAlertOptions>(configuration.GetSection(OpsAlertOptions.SectionName));
        services.AddHttpClient<IOpsAlerts, DiscordOpsAlerts>(client => client.Timeout = TimeSpan.FromSeconds(10));

        return services;
    }
}

/// <summary>Per-request / per-invocation workspace scope. Set by the API (from auth) or by the worker (from the queue message).</summary>
public sealed class AmbientWorkspace : ICurrentWorkspace
{
    public int? WorkspaceId { get; set; }
}
