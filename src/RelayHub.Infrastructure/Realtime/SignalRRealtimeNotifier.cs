using System.Text.Json;
using Azure.Core.Serialization;
using Azure.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.Azure.SignalR.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;

namespace RelayHub.Infrastructure.Realtime;

public sealed class SignalROptions
{
    public const string SectionName = "SignalR";

    /// <summary>Azure: <c>https://{name}.service.signalr.net</c>, used with Managed Identity (DefaultAzureCredential; role "SignalR Service Owner").</summary>
    public string? Endpoint { get; set; }

    /// <summary>Local development: the access-key connection string of the SignalR resource. Secret, never in the repo.</summary>
    public string? ConnectionString { get; set; }

    public string HubName { get; set; } = "relayhub";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) || !string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>Hub → client message names and payloads (the dashboard contract). Group per workspace: <c>ws-{workspaceId}</c>.</summary>
public static class RealtimeMessages
{
    public const string EventCreated = "eventCreated";
    public const string DeliveryUpdated = "deliveryUpdated";
    public const string ReplySent = "replySent";

    public static string Group(int workspaceId) => $"ws-{workspaceId}";

    public sealed record EventCreatedMessage(long EventId, int? ConversationId, string Channel, string Direction, string? SenderName, string? SenderHandle,
        string Text, DateTimeOffset ReceivedAt, string? SourceName);

    public sealed record DeliveryUpdatedMessage(long DeliveryId, long EventId, int DestinationId, string Status, int AttemptCount, string? LastError, DateTimeOffset? NextAttemptAt);

    public sealed record ReplySentMessage(long EventId, int? ConversationId, string Channel, string Text, DateTimeOffset At, string? ProviderMessageId);

    public static EventCreatedMessage ForEvent(Event evt, string? sourceName) => new(
        evt.Id, evt.ConversationId, evt.Channel, evt.Direction.ToString().ToLowerInvariant(), evt.SenderName, evt.SenderHandle, evt.Text, evt.ReceivedAt, sourceName);

    public static DeliveryUpdatedMessage ForDelivery(Delivery d) => new(d.Id, d.EventId, d.DestinationId, d.Status.ToString(), d.AttemptCount, d.LastError, d.NextAttemptAt);

    public static ReplySentMessage ForReply(Event reply) => new(reply.Id, reply.ConversationId, reply.Channel, reply.Text, reply.ReceivedAt, reply.ProviderMessageId);
}

/// <summary>What a dashboard needs to connect to the hub: the client URL and a short-lived access token.</summary>
public sealed record RealtimeNegotiation(string Url, string AccessToken);

/// <summary>Serves <c>POST /api/v1/realtime/negotiate</c>. Registered only when SignalR is configured; absent → 503 realtime-unavailable.</summary>
public interface IRealtimeNegotiator
{
    /// <summary>Negotiates for the given user and puts the user in the workspace group (user groups persist across connections).</summary>
    Task<RealtimeNegotiation> NegotiateAsync(string userId, int workspaceId, CancellationToken ct);
}

/// <summary>
/// Azure SignalR Service in serverless mode through the Management SDK (REST, Transient transport): no hub class, no server
/// connection, each message is one HTTPS call. Shared by the API (replySent, negotiate) and the Functions worker (eventCreated,
/// deliveryUpdated). The hub context is created once and reused. Never throws: a failed notification is a warning, never a
/// failed request or a poisoned queue message.
/// </summary>
public sealed class SignalRRealtimeNotifier(IOptions<SignalROptions> options, ILoggerFactory loggerFactory, ILogger<SignalRRealtimeNotifier> log)
    : IRealtimeNotifier, IRealtimeNegotiator, IAsyncDisposable
{
    private static readonly TimeSpan NegotiatedTokenLifetime = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServiceManager? _manager;
    private ServiceHubContext? _hub;

    public Task EventCreatedAsync(Event evt, CancellationToken ct) => EventCreatedAsync(evt, null, ct);

    public Task EventCreatedAsync(Event evt, string? sourceName, CancellationToken ct)
        => SendAsync(evt.WorkspaceId, RealtimeMessages.EventCreated, RealtimeMessages.ForEvent(evt, sourceName), ct);

    public Task DeliveryUpdatedAsync(Delivery delivery, int workspaceId, CancellationToken ct)
        => SendAsync(workspaceId, RealtimeMessages.DeliveryUpdated, RealtimeMessages.ForDelivery(delivery), ct);

    public Task ReplySentAsync(Event reply, CancellationToken ct)
        => SendAsync(reply.WorkspaceId, RealtimeMessages.ReplySent, RealtimeMessages.ForReply(reply), ct);

    public async Task<RealtimeNegotiation> NegotiateAsync(string userId, int workspaceId, CancellationToken ct)
    {
        // Negotiate may throw: the caller is an HTTP request that should answer 503 when the service is unreachable.
        var hub = await GetHubAsync(ct);
        var response = await hub.NegotiateAsync(new NegotiationOptions { UserId = userId, TokenLifetime = NegotiatedTokenLifetime }, ct);
        await hub.UserGroups.AddToGroupAsync(userId, RealtimeMessages.Group(workspaceId), ct);
        return new RealtimeNegotiation(response.Url ?? "", response.AccessToken ?? "");
    }

    private async Task SendAsync(int workspaceId, string method, object payload, CancellationToken ct)
    {
        try
        {
            var hub = await GetHubAsync(ct);
            await hub.Clients.Group(RealtimeMessages.Group(workspaceId)).SendAsync(method, payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Realtime {Method} to workspace {WorkspaceId} failed; dashboards will catch up on refresh", method, workspaceId);
        }
    }

    private async Task<ServiceHubContext> GetHubAsync(CancellationToken ct)
    {
        if (_hub is { } ready) return ready;
        await _gate.WaitAsync(ct);
        try
        {
            if (_hub is { } created) return created;
            var o = options.Value;
            if (!o.IsConfigured)
                throw new InvalidOperationException("Configure SignalR:Endpoint (Azure, Managed Identity) or SignalR:ConnectionString (local).");

            _manager ??= new ServiceManagerBuilder()
                .WithOptions(m =>
                {
                    if (!string.IsNullOrWhiteSpace(o.ConnectionString))
                        m.ConnectionString = o.ConnectionString;
                    else
                        m.ServiceEndpoints = [new ServiceEndpoint(new Uri(o.Endpoint!), new DefaultAzureCredential())];
                    m.ServiceTransportType = ServiceTransportType.Transient;
                    m.HttpClientTimeout = TimeSpan.FromSeconds(10);
                    // Same wire format as the REST API: camelCase, so the dashboard reads payload.eventId, not payload.EventId.
                    m.UseJsonObjectSerializer(new JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                })
                .WithLoggerFactory(loggerFactory)
                .BuildServiceManager();

            _hub = await _manager.CreateHubContextAsync(o.HubName, ct);
            log.LogInformation("Realtime: connected to Azure SignalR hub '{Hub}' ({Mode})", o.HubName, string.IsNullOrWhiteSpace(o.ConnectionString) ? "identity" : "access key");
            return _hub;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is { } hub) await hub.DisposeAsync();
        _manager?.Dispose();
        _gate.Dispose();
    }
}
