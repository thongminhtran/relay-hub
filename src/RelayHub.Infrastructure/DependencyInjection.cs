using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Alerts;
using RelayHub.Infrastructure.Channels;
using RelayHub.Infrastructure.Connectors;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;
using RelayHub.Infrastructure.Messaging;
using RelayHub.Infrastructure.Processing;
using RelayHub.Infrastructure.Processing.Parsers;
using RelayHub.Infrastructure.Replies;

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
        services.AddChannelConnectors();
        services.AddReplyChannels();
        // Live dashboard updates are optional; the SignalR implementation replaces this when configured (TryAdd keeps the override).
        services.TryAddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();
        services.AddScoped<DeliveryDispatcher>();
        services.AddScoped<ISourceParser, GenericParser>();
        services.AddScoped<ISourceParser, MetaWebhookParser>();
        services.AddScoped<ISourceParser, TelegramUpdateParser>();
        services.AddScoped<ISourceParser, EmailParser>();
        services.AddScoped<IncomingProcessor>();
        services.AddScoped<DeliveryProcessor>();

        return services;
    }

    /// <summary>Week 4 notification connectors: Teams (Workflows webhook), email (Resend), Telegram bot, generic HTTP with the SSRF guard.</summary>
    private static IServiceCollection AddChannelConnectors(this IServiceCollection services)
    {
        var timeout = TimeSpan.FromSeconds(10);
        services.AddHttpClient<TeamsConnector>(client => client.Timeout = timeout);
        services.AddScoped<IDestinationConnector>(sp => sp.GetRequiredService<TeamsConnector>());
        services.AddHttpClient<EmailConnector>(client => client.Timeout = timeout);
        services.AddScoped<IDestinationConnector>(sp => sp.GetRequiredService<EmailConnector>());
        services.AddHttpClient<TelegramConnector>(client => client.Timeout = timeout);
        services.AddScoped<IDestinationConnector>(sp => sp.GetRequiredService<TelegramConnector>());
        // Owner-configured URLs: no redirects, and the connection is pinned to an address the SSRF guard accepted.
        services.AddHttpClient<HttpConnector>(client => client.Timeout = timeout)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    ConnectCallback = HttpConnector.ConnectGuardedAsync,
                });
        services.AddScoped<IDestinationConnector>(sp => sp.GetRequiredService<HttpConnector>());
        return services;
    }

    /// <summary>Reply channels (synchronous, used by the API's reply endpoint): Meta Send API for Instagram + Facebook, Telegram, Resend email (also for web-form conversations).</summary>
    private static IServiceCollection AddReplyChannels(this IServiceCollection services)
    {
        var timeout = TimeSpan.FromSeconds(10);
        services.AddHttpClient(MetaReplyChannel.HttpClientName, client => client.Timeout = timeout);
        services.AddHttpClient<TelegramReplyChannel>(client => client.Timeout = timeout);
        services.AddHttpClient(EmailReplyChannel.HttpClientName, client => client.Timeout = timeout);
        services.AddScoped<ReplyThreadLookup>();

        services.AddScoped<IReplyChannel>(sp => MetaChannel(sp, SourceType.Instagram));
        services.AddScoped<IReplyChannel>(sp => MetaChannel(sp, SourceType.Facebook));
        services.AddScoped<IReplyChannel>(sp => sp.GetRequiredService<TelegramReplyChannel>());
        services.AddScoped<IReplyChannel>(sp => EmailChannel(sp, SourceType.Email));
        services.AddScoped<IReplyChannel>(sp => EmailChannel(sp, SourceType.Generic));
        return services;

        static MetaReplyChannel MetaChannel(IServiceProvider sp, SourceType type) => new(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(MetaReplyChannel.HttpClientName), type, sp.GetRequiredService<ILogger<MetaReplyChannel>>());

        static EmailReplyChannel EmailChannel(IServiceProvider sp, SourceType type) => new(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(EmailReplyChannel.HttpClientName), sp.GetRequiredService<IOptions<ChannelOptions>>(),
            sp.GetRequiredService<ReplyThreadLookup>(), type, sp.GetRequiredService<ILogger<EmailReplyChannel>>());
    }

    /// <summary>Storage Queues (section "Storage") and operator alerts ("RelayHub:OpsDiscordWebhookUrl").</summary>
    public static IServiceCollection AddRelayHubMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.AddSingleton<IRelayQueues, StorageQueues>();

        services.Configure<OpsAlertOptions>(configuration.GetSection(OpsAlertOptions.SectionName));
        services.AddHttpClient<IOpsAlerts, DiscordOpsAlerts>(client => client.Timeout = TimeSpan.FromSeconds(10));

        // Provider secrets for the hooks (API) and the Resend / Telegram senders (worker + API). All optional; one startup warning lists what is unset.
        services.Configure<ChannelOptions>(configuration.GetSection(ChannelOptions.SectionName));
        services.AddHostedService<ChannelOptionsStartupCheck>();

        return services;
    }
}

/// <summary>Per-request / per-invocation workspace scope. Set by the API (from auth) or by the worker (from the queue message).</summary>
public sealed class AmbientWorkspace : ICurrentWorkspace
{
    public int? WorkspaceId { get; set; }
}
