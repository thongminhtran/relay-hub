using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
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

/// <summary>Real API + real SQL Server in Docker (Testcontainers). Queues, connectors and alerts are faked; the worker's processors are the real ones.</summary>
public sealed class SqlServerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public RecordingConnector Connector { get; } = new();
    public InMemoryQueues Queues { get; } = new();
    public RecordingAlerts Alerts { get; } = new();

    public Task InitializeAsync() => _sql.StartAsync();

    Task IAsyncLifetime.DisposeAsync() => _sql.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:RelayHub", _sql.GetConnectionString());
        builder.UseSetting("RelayHub:ApiKeyMasterKey", "integration-test-master-key");
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
        });
    }

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

[Trait("Category", "Docker")]
public class IngestFlowTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private static HttpRequestMessage Ingest(string sourceKey, string apiKey, object body, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/hooks/{sourceKey}") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", apiKey);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    private async Task<(string ApiKey, int SourceId, int DestinationId)> ConfigureAsync(HttpClient client, string sourceName)
    {
        var created = await client.PostAsJsonAsync("/api/v1/sources", new { name = sourceName });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = await created.Content.ReadFromJsonAsync<JsonElement>();
        var apiKey = source.GetProperty("apiKey").GetString()!;
        var sourceId = source.GetProperty("source").GetProperty("id").GetInt32();

        var destCreated = await client.PostAsJsonAsync("/api/v1/destinations", new
        {
            name = $"Discord for {sourceName}", type = "discord",
            config = new { webhookUrl = "https://discord.com/api/webhooks/123456/secret-token-value" },
        });
        Assert.Equal(HttpStatusCode.Created, destCreated.StatusCode);
        var dest = await destCreated.Content.ReadFromJsonAsync<JsonElement>();
        var destinationId = dest.GetProperty("id").GetInt32();
        Assert.DoesNotContain("secret-token-value", dest.GetProperty("config").GetRawText()); // secrets are masked

        var routeCreated = await client.PostAsJsonAsync("/api/v1/routes", new { sourceId, destinationId, template = "{{name}}: {{text}}" });
        Assert.Equal(HttpStatusCode.Created, routeCreated.StatusCode);
        return (apiKey, sourceId, destinationId);
    }

    [Fact]
    public async Task Form_message_is_queued_stored_routed_delivered_and_deduplicated()
    {
        using var client = factory.CreateClient();
        var (apiKey, _, _) = await ConfigureAsync(client, "contact-form");
        factory.Connector.Sent.Clear();

        // Ingest answers 202 "queued" without touching SQL: nothing is stored yet, the message sits on the incoming queue.
        var accepted = await client.SendAsync(Ingest("contact-form", apiKey, new { name = "Anna", email = "anna@example.com", message = "Do you build websites?" }, "form-0001"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queued", body.GetProperty("status").GetString());
        var correlationId = body.GetProperty("correlationId").GetString();
        Assert.Single(factory.Queues.Incoming);
        Assert.Equal(correlationId, factory.Queues.Incoming.Peek().CorrelationId);
        Assert.Empty(factory.Connector.Sent);

        // The worker stores the event, creates one delivery per route, and the delivery message sends it.
        var run = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Stored], run.Incoming);
        Assert.Equal([DeliveryOutcome.Delivered], run.Deliveries);
        Assert.Single(factory.Connector.Sent);
        Assert.Equal(("Discord for contact-form", "Anna: Do you build websites?"), factory.Connector.Sent[0]);

        // Same Idempotency-Key again -> queued again, but the worker ignores it (unique index on SourceId + ExternalId).
        var dupResponse = await client.SendAsync(Ingest("contact-form", apiKey, new { name = "Anna", email = "anna@example.com", message = "Do you build websites?" }, "form-0001"));
        Assert.Equal(HttpStatusCode.Accepted, dupResponse.StatusCode);
        run = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Duplicate], run.Incoming);
        Assert.Empty(run.Deliveries);
        Assert.Single(factory.Connector.Sent);

        // Inbox shows one event, delivered, with the correlation id of the original request.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/events?q=websites");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items")[0];
        Assert.Equal("web", item.GetProperty("channel").GetString());
        Assert.Equal(correlationId, item.GetProperty("correlationId").GetString());
        Assert.Equal("Delivered", item.GetProperty("deliveries")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Failing_destination_backs_off_dead_letters_alerts_and_can_be_replayed()
    {
        using var client = factory.CreateClient();
        var (apiKey, _, _) = await ConfigureAsync(client, "flaky-form");
        factory.Alerts.Alerts.Clear();

        factory.Connector.FailWithStatus = 503;
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Ingest("flaky-form", apiKey, new { name = "Bao", message = "Hello" }, "flaky-0001"))).StatusCode);

            // Attempt 1 fails -> Retrying, re-enqueued with the first backoff (30 s). The test does not wait: it drains the queue.
            var incomingRun = await factory.RunWorkerAsync();
            Assert.Equal([IncomingOutcome.Stored], incomingRun.Incoming);
            Assert.Equal([DeliveryOutcome.Retrying, DeliveryOutcome.Retrying, DeliveryOutcome.Retrying, DeliveryOutcome.Retrying, DeliveryOutcome.DeadLettered], incomingRun.Deliveries);
        }
        finally
        {
            factory.Connector.FailWithStatus = 0;
        }

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/events?q=Hello");
        var eventId = list.GetProperty("items")[0].GetProperty("id").GetInt64();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{eventId}");
        var delivery = detail.GetProperty("deliveries")[0];
        var deliveryId = delivery.GetProperty("id").GetInt64();
        Assert.Equal("DeadLettered", delivery.GetProperty("status").GetString());
        Assert.Equal(5, delivery.GetProperty("attemptCount").GetInt32());
        Assert.Equal(5, delivery.GetProperty("attempts").GetArrayLength());
        Assert.Equal(503, delivery.GetProperty("attempts")[0].GetProperty("httpStatus").GetInt32());
        Assert.Contains("503", delivery.GetProperty("lastError").GetString());

        // The operator was told exactly once, with the replay instruction.
        var alert = Assert.Single(factory.Alerts.Alerts);
        Assert.Contains($"Delivery {deliveryId} dead-lettered", alert.Title);
        Assert.Contains($"/api/v1/deliveries/{deliveryId}/replay", alert.Detail);

        // Replay: one more attempt, history kept. The destination works again -> Delivered on attempt 6.
        var replay = await client.PostAsync($"/api/v1/deliveries/{deliveryId}/replay", null);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("Pending", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var replayRun = await factory.RunWorkerAsync();
        Assert.Equal([DeliveryOutcome.Delivered], replayRun.Deliveries);

        var after = await client.GetFromJsonAsync<JsonElement>($"/api/v1/deliveries/{deliveryId}");
        Assert.Equal("Delivered", after.GetProperty("status").GetString());
        Assert.Equal(6, after.GetProperty("attemptCount").GetInt32());
        Assert.Contains(factory.Connector.Sent, s => s.Text == "Bao: Hello");

        // A delivered delivery cannot be replayed.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/deliveries/{deliveryId}/replay", null)).StatusCode);
    }

    [Fact]
    public async Task Stale_retry_message_is_skipped_after_a_replay_overtook_it()
    {
        using var client = factory.CreateClient();
        var (apiKey, _, _) = await ConfigureAsync(client, "stale-form");

        factory.Connector.FailWithStatus = 500;
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Ingest("stale-form", apiKey, new { name = "Chi", message = "Ping" }, "stale-0001"))).StatusCode);

        // Run only the incoming message and the first delivery attempt by hand, keeping the scheduled retry in the queue.
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(IncomingOutcome.Stored, await scope.ServiceProvider.GetRequiredService<IncomingProcessor>().ProcessAsync(factory.Queues.Incoming.Dequeue(), CancellationToken.None));
        var first = factory.Queues.Deliveries.Dequeue();
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(DeliveryOutcome.Retrying, await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(first.Message, CancellationToken.None));
        var scheduledRetry = factory.Queues.Deliveries.Dequeue();
        Assert.Equal(TimeSpan.FromSeconds(30), scheduledRetry.Delay);
        Assert.Equal(1, scheduledRetry.Message.AfterAttempt);
        factory.Connector.FailWithStatus = 0;

        // The owner replays before the 30 s retry fires; the replay succeeds...
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/deliveries/{first.Message.DeliveryId}/replay", null)).StatusCode);
        var replayRun = await factory.RunWorkerAsync();
        Assert.Equal([DeliveryOutcome.Delivered], replayRun.Deliveries);

        // ...and when the old retry message finally shows up it is recognised as stale and does not send a duplicate.
        var sentBefore = factory.Connector.Sent.Count;
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(DeliveryOutcome.Skipped, await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(scheduledRetry.Message, CancellationToken.None));
        Assert.Equal(sentBefore, factory.Connector.Sent.Count);
    }

    [Fact]
    public async Task Wrong_api_key_is_rejected_and_wrong_source_key_is_dropped_by_the_worker()
    {
        using var client = factory.CreateClient();
        var (apiKey, _, _) = await ConfigureAsync(client, "guarded-form");
        var before = (await client.GetFromJsonAsync<JsonElement>("/api/v1/events")).GetProperty("total").GetInt32();
        factory.Queues.Incoming.Clear();

        // Forged signature: rejected at the edge, nothing queued.
        var forged = await client.SendAsync(Ingest("guarded-form", "src_000000000000_00000000000000000000000000000000", new { name = "Mallory", message = "forged" }));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Empty(factory.Queues.Incoming);

        // Valid key used on another source's URL: accepted at the edge (no DB on the hot path), dropped by the worker.
        var mismatched = await client.SendAsync(Ingest("contact-form", apiKey, new { name = "Mallory", message = "wrong door" }));
        Assert.Equal(HttpStatusCode.Accepted, mismatched.StatusCode);
        var run = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Dropped], run.Incoming);

        // Not JSON object / too large: rejected at the edge.
        using var array = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/guarded-form") { Content = JsonContent.Create(new[] { 1, 2 }) };
        array.Headers.Add("X-Api-Key", apiKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(array)).StatusCode);
        using var huge = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/guarded-form") { Content = JsonContent.Create(new { message = new string('x', 40_000) }) };
        huge.Headers.Add("X-Api-Key", apiKey);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(huge)).StatusCode);

        var after = (await client.GetFromJsonAsync<JsonElement>("/api/v1/events")).GetProperty("total").GetInt32();
        Assert.Equal(before, after);
    }
}
