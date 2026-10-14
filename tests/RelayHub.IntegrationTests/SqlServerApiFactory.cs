using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RelayHub.Api.Startup;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Processing;
using Testcontainers.MsSql;

namespace RelayHub.IntegrationTests;

/// <summary>Fake Discord: records what would have been posted, and can fail on demand to exercise retry bookkeeping.</summary>
public sealed class RecordingConnector : IDestinationConnector
{
    public List<(string Destination, string Text)> Sent { get; } = [];

    /// <summary>While non-zero, every send fails with this HTTP status (429/5xx = retryable, 4xx = not).</summary>
    public int FailWithStatus { get; set; }

    public DestinationType Type => DestinationType.Discord;

    public Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        if (FailWithStatus != 0)
            return Task.FromResult(new DeliveryResult(false, FailWithStatus, $"{FailWithStatus} simulated failure", Retryable: FailWithStatus == 429 || FailWithStatus >= 500));
        Sent.Add((destination.Name, text));
        return Task.FromResult(DeliveryResult.Ok(204));
    }
}

/// <summary>Stand-in for Storage Queues: messages pile up here until the test "runs the worker".</summary>
public sealed class InMemoryQueues : IRelayQueues
{
    public Queue<IncomingMessage> Incoming { get; } = new();
    public Queue<(DeliveryMessage Message, TimeSpan? Delay)> Deliveries { get; } = new();

    public Task EnqueueIncomingAsync(IncomingMessage message, CancellationToken ct) { Incoming.Enqueue(message); return Task.CompletedTask; }

    public Task EnqueueDeliveryAsync(DeliveryMessage message, TimeSpan? visibilityDelay, CancellationToken ct) { Deliveries.Enqueue((message, visibilityDelay)); return Task.CompletedTask; }
}

public sealed class RecordingAlerts : IOpsAlerts
{
    public List<(string Title, string Detail)> Alerts { get; } = [];

    public Task NotifyAsync(string title, string detail, CancellationToken ct) { Alerts.Add((title, detail)); return Task.CompletedTask; }
}

/// <summary>Stand-in for Azure SignalR: records what the dashboards would have received.</summary>
public sealed class RecordingNotifier : IRealtimeNotifier
{
    public List<(long EventId, int WorkspaceId, string? SourceName)> EventsCreated { get; } = [];
    public List<(long DeliveryId, int WorkspaceId, DeliveryStatus Status, int AttemptCount)> DeliveriesUpdated { get; } = [];
    public List<(long EventId, int WorkspaceId, string Text)> RepliesSent { get; } = [];

    public Task EventCreatedAsync(Event evt, CancellationToken ct) => EventCreatedAsync(evt, null, ct);

    public Task EventCreatedAsync(Event evt, string? sourceName, CancellationToken ct) { EventsCreated.Add((evt.Id, evt.WorkspaceId, sourceName)); return Task.CompletedTask; }

    public Task DeliveryUpdatedAsync(Delivery delivery, int workspaceId, CancellationToken ct) { DeliveriesUpdated.Add((delivery.Id, workspaceId, delivery.Status, delivery.AttemptCount)); return Task.CompletedTask; }

    public Task ReplySentAsync(Event reply, CancellationToken ct) { RepliesSent.Add((reply.Id, reply.WorkspaceId, reply.Text)); return Task.CompletedTask; }
}

/// <summary>
/// Real API + real SQL Server in Docker (Testcontainers). Queues, connectors, alerts and SignalR are faked; the worker's processors are
/// the real ones. Shared by every Docker test class through the "SqlServer" collection (one container per test run, tests run sequentially).
/// </summary>
public sealed class SqlServerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private string? _ownerToken;
    private string? _demoToken;

    public RecordingConnector Connector { get; } = new();
    public InMemoryQueues Queues { get; } = new();
    public RecordingAlerts Alerts { get; } = new();
    public RecordingNotifier Notifier { get; } = new();

    public Task InitializeAsync() => _sql.StartAsync();

    Task IAsyncLifetime.DisposeAsync() => _sql.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:RelayHub", _sql.GetConnectionString());
        builder.UseSetting("RelayHub:ApiKeyMasterKey", "integration-test-master-key");
        builder.UseSetting("RelayHub:JwtSigningKey", "integration-test-jwt-signing-key-with-plenty-of-length");
        builder.UseSetting("RelayHub:MigrateOnStartup", "true");
        builder.UseSetting("RelayHub:SeedOnStartup", "true");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDestinationConnector>();
            services.AddSingleton<IDestinationConnector>(Connector);
            services.RemoveAll<IRelayQueues>();
            services.AddSingleton<IRelayQueues>(Queues);
            services.RemoveAll<IOpsAlerts>();
            services.AddSingleton<IOpsAlerts>(Alerts);
            services.RemoveAll<IRealtimeNotifier>();
            services.AddSingleton<IRealtimeNotifier>(Notifier);
        });
    }

    /// <summary>A client signed in as the development owner (Studio workspace). The token is obtained once per factory.</summary>
    public async Task<HttpClient> CreateOwnerClientAsync()
        => WithBearer(CreateClient(), _ownerToken ??= await LoginAsync(DevSeeder.DevOwnerEmail, DevSeeder.DevOwnerPassword));

    /// <summary>A client holding an anonymous Demo token (Demo workspace, role Demo).</summary>
    public async Task<HttpClient> CreateDemoClientAsync()
    {
        if (_demoToken is null)
        {
            using var anonymous = CreateClient();
            var response = await anonymous.PostAsJsonAsync("/api/v1/auth/demo", new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _demoToken = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }
        return WithBearer(CreateClient(), _demoToken);
    }

    public async Task<string> LoginAsync(string email, string password)
    {
        using var anonymous = CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    public static HttpClient WithBearer(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Runs work against the database without a workspace scope (what the seeder and the worker do).</summary>
    public async Task<T> WithDbAsync<T>(Func<RelayHubDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmbientWorkspace>().WorkspaceId = null;
        return await work(scope.ServiceProvider.GetRequiredService<RelayHubDbContext>());
    }

    public Task<Workspace> DemoWorkspaceAsync() => WithDbAsync(db => db.Workspaces.SingleAsync(w => w.IsDemo));

    public Task<Workspace> StudioWorkspaceAsync() => WithDbAsync(db => db.Workspaces.SingleAsync(w => w.Name == "Studio"));

    /// <summary>Seeds a source + conversation + one inbound event directly (what the worker would have stored for a provider webhook).</summary>
    public Task<(Source Source, Conversation Conversation, Event Event)> SeedConversationAsync(
        int workspaceId, SourceType type, string sourceName, string threadId, string? senderName, string? senderHandle, DateTimeOffset lastInboundAt, string text)
        => WithDbAsync(async db =>
        {
            var source = await db.Sources.SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Name == sourceName)
                         ?? new Source { WorkspaceId = workspaceId, Type = type, Name = sourceName, KeyId = Core.Security.ApiKeys.NewKeyId() };
            if (source.Id == 0) { db.Sources.Add(source); await db.SaveChangesAsync(); }

            var conversation = new Conversation
            {
                WorkspaceId = workspaceId, SourceId = source.Id, ExternalThreadId = threadId, SenderName = senderName, SenderHandle = senderHandle, LastInboundAt = lastInboundAt,
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();

            var evt = new Event
            {
                WorkspaceId = workspaceId, SourceId = source.Id, ConversationId = conversation.Id, ExternalId = Guid.NewGuid().ToString("N"),
                Channel = ChannelNames.For(type), SenderName = senderName, SenderHandle = senderHandle, Text = text, ReceivedAt = lastInboundAt,
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
            db.Events.Add(evt);
            await db.SaveChangesAsync();
            return (source, conversation, evt);
        });

    /// <summary>What the Functions worker would do: drain both queues, one scope per message, ignoring visibility delays.</summary>
    public async Task<(List<IncomingOutcome> Incoming, List<DeliveryOutcome> Deliveries)> RunWorkerAsync()
    {
        var incoming = new List<IncomingOutcome>();
        var deliveries = new List<DeliveryOutcome>();
        while (Queues.Incoming.Count > 0 || Queues.Deliveries.Count > 0)
        {
            while (Queues.Incoming.TryDequeue(out var msg))
            {
                using var scope = Services.CreateScope();
                incoming.Add(await scope.ServiceProvider.GetRequiredService<IncomingProcessor>().ProcessAsync(msg, CancellationToken.None));
            }
            while (Queues.Deliveries.TryDequeue(out var item))
            {
                using var scope = Services.CreateScope();
                deliveries.Add(await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(item.Message, CancellationToken.None));
            }
        }
        return (incoming, deliveries);
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerApiFactory>
{
    public const string Name = "SqlServer";
}
