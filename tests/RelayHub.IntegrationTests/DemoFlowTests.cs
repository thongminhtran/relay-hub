using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayHub.Core.Demo;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Processing;

namespace RelayHub.IntegrationTests;

/// <summary>
/// The public demo workspace against real SQL Server: seeded data, simulate → worker → mock sink, the failure switch driving
/// retry → dead-letter → replay on the demo backoff, the 500-event cap, and the demo-only guard.
/// </summary>
[Trait("Category", "Docker")]
public class DemoFlowTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    private const int DemoWorkspaceId = 2;

    private HttpClient DemoClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Workspace-Id", DemoWorkspaceId.ToString());
        return client;
    }

    private void ResetFakes()
    {
        factory.Queues.Incoming.Clear();
        factory.Queues.Deliveries.Clear();
        factory.Connector.Sent.Clear();
        factory.Alerts.Alerts.Clear();
    }

    private async Task<IncomingOutcome> ProcessIncomingAsync(IncomingMessage msg)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IncomingProcessor>().ProcessAsync(msg, CancellationToken.None);
    }

    private async Task<DeliveryOutcome> ProcessDeliveryAsync(DeliveryMessage msg)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessAsync(msg, CancellationToken.None);
    }

    private static async Task SetFailureModeAsync(HttpClient client, bool enabled)
    {
        var response = await client.PutAsJsonAsync("/api/v1/demo/failure-mode", new { enabled });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(enabled, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        Assert.Equal(enabled, (await client.GetFromJsonAsync<JsonElement>("/api/v1/demo/failure-mode")).GetProperty("enabled").GetBoolean());
    }

    /// <summary>POST /simulate for the web channel; returns the correlation id the API answered with.</summary>
    private static async Task<string> SimulateWebAsync(HttpClient client, string text, string senderName)
    {
        var response = await client.PostAsJsonAsync("/api/v1/demo/simulate", new { channel = "web", text, senderName });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("web", body.GetProperty("channel").GetString());
        Assert.Equal(senderName, body.GetProperty("sender").GetProperty("name").GetString());
        Assert.Equal(text, body.GetProperty("text").GetString());
        Assert.Contains("inbox", body.GetProperty("hint").GetString());
        return body.GetProperty("correlationId").GetString()!;
    }

    [Fact]
    public async Task Seed_creates_the_demo_configuration_and_a_week_of_sample_conversations()
    {
        using var client = DemoClient();

        var sources = await client.GetFromJsonAsync<JsonElement>("/api/v1/sources");
        string[] expectedSources = [DemoCatalog.WebsiteSource, DemoCatalog.InstagramSource, DemoCatalog.FacebookSource, DemoCatalog.TelegramSource, DemoCatalog.EmailSource];
        Assert.Equal(expectedSources.Order(), sources.EnumerateArray().Select(s => s.GetProperty("name").GetString()!).Order());
        Assert.Equal("instagram", sources.EnumerateArray().Single(s => s.GetProperty("name").GetString() == DemoCatalog.InstagramSource).GetProperty("type").GetString());

        var destinations = await client.GetFromJsonAsync<JsonElement>("/api/v1/destinations");
        Assert.Equal(2, destinations.GetArrayLength());
        var flaky = destinations.EnumerateArray().Single(d => d.GetProperty("name").GetString() == DemoCatalog.FlakyDestination);
        Assert.False(flaky.GetProperty("config").GetProperty("failing").GetBoolean());
        Assert.Contains(destinations.EnumerateArray(), d => d.GetProperty("name").GetString() == DemoCatalog.DiscordDestination);

        var routes = await client.GetFromJsonAsync<JsonElement>("/api/v1/routes");
        Assert.Equal(6, routes.GetArrayLength()); // 5 sources → Discord (mock), website → Flaky as well

        var events = await client.GetFromJsonAsync<JsonElement>("/api/v1/events?pageSize=100");
        Assert.True(events.GetProperty("total").GetInt32() > 20);
        var items = events.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(DemoChannels.All.Order(), items.Select(e => e.GetProperty("channel").GetString()!).Distinct().Order());
        Assert.Contains(items, e => e.GetProperty("direction").GetString() == "out" && e.GetProperty("senderName").GetString() == "You");
        Assert.All(items.Where(e => e.GetProperty("direction").GetString() == "in"), e => Assert.True(e.GetProperty("deliveries").GetArrayLength() >= 1));

        // The timeline screen has something to show: one delivery still retrying, one dead-lettered, the rest delivered.
        var statuses = items.SelectMany(e => e.GetProperty("deliveries").EnumerateArray()).Select(d => d.GetProperty("status").GetString()).ToList();
        Assert.Single(statuses, "Retrying");
        Assert.Single(statuses, "DeadLettered");
        Assert.True(statuses.Count(s => s == "Delivered") > 20);

        // Nothing of this leaks into the Studio workspace.
        using var studio = factory.CreateClient();
        var studioSources = await studio.GetFromJsonAsync<JsonElement>("/api/v1/sources");
        Assert.DoesNotContain(studioSources.EnumerateArray(), s => s.GetProperty("name").GetString()!.StartsWith("demo-"));
    }

    [Fact]
    public async Task Simulated_web_message_flows_through_the_worker_to_the_mock_sink_and_the_failure_switch_drives_retry_dead_letter_replay()
    {
        ResetFakes();
        using var client = DemoClient();
        await SetFailureModeAsync(client, false);

        // 1. Simulate: the API only queues; the message looks like the real website hook's.
        var correlationId = await SimulateWebAsync(client, "Do you build websites for bakeries?", "Anna Nguyen");
        var queued = Assert.Single(factory.Queues.Incoming);
        Assert.Equal(IncomingKinds.Generic, queued.Kind);
        Assert.Equal(DemoCatalog.WebsiteSource, queued.SourceKey);
        Assert.False(string.IsNullOrEmpty(queued.KeyId));
        Assert.Equal(correlationId, queued.CorrelationId);

        // 2. Worker stores it in the demo workspace with one delivery per route (Discord mock + Flaky).
        Assert.Equal(IncomingOutcome.Stored, await ProcessIncomingAsync(factory.Queues.Incoming.Dequeue()));
        Assert.Equal(2, factory.Queues.Deliveries.Count);
        Assert.All(factory.Queues.Deliveries, d => Assert.Equal(DemoWorkspaceId, d.Message.WorkspaceId));

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/events?q=bakeries");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var stored = list.GetProperty("items")[0];
        Assert.Equal(correlationId, stored.GetProperty("correlationId").GetString());
        Assert.Equal("Anna Nguyen", stored.GetProperty("senderName").GetString());
        var eventId = stored.GetProperty("id").GetInt64();

        // 3. Both deliveries succeed through the sink; the real Discord connector is never called.
        while (factory.Queues.Deliveries.TryDequeue(out var item))
            Assert.Equal(DeliveryOutcome.Delivered, await ProcessDeliveryAsync(item.Message));
        Assert.Empty(factory.Connector.Sent);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{eventId}");
        Assert.Equal(2, detail.GetProperty("deliveries").GetArrayLength());
        Assert.All(detail.GetProperty("deliveries").EnumerateArray(), d =>
        {
            Assert.Equal("Delivered", d.GetProperty("status").GetString());
            Assert.Equal(200, d.GetProperty("attempts")[0].GetProperty("httpStatus").GetInt32());
            Assert.True(d.GetProperty("attempts")[0].GetProperty("durationMs").GetInt32() >= 30);
        });

        // "Send test" in the demo workspace is mocked as well.
        var destinations = await client.GetFromJsonAsync<JsonElement>("/api/v1/destinations");
        var discordId = destinations.EnumerateArray().Single(d => d.GetProperty("name").GetString() == DemoCatalog.DiscordDestination).GetProperty("id").GetInt32();
        var flakyId = destinations.EnumerateArray().Single(d => d.GetProperty("name").GetString() == DemoCatalog.FlakyDestination).GetProperty("id").GetInt32();
        var test = await (await client.PostAsync($"/api/v1/destinations/{discordId}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(test.GetProperty("succeeded").GetBoolean());
        Assert.Equal(200, test.GetProperty("httpStatus").GetInt32());
        Assert.Empty(factory.Connector.Sent);

        // 4. Failure switch on: the flaky destination answers 503, the demo backoff (2/4/8/16 s) runs and the delivery dead-letters.
        await SetFailureModeAsync(client, true);
        var flakyTest = await (await client.PostAsync($"/api/v1/destinations/{flakyId}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(flakyTest.GetProperty("succeeded").GetBoolean());
        Assert.Equal(503, flakyTest.GetProperty("httpStatus").GetInt32());

        await SimulateWebAsync(client, "Please fail this one", "Marco Rossi");
        Assert.Equal(IncomingOutcome.Stored, await ProcessIncomingAsync(factory.Queues.Incoming.Dequeue()));
        var firstRound = new List<DeliveryOutcome>();
        var initial = factory.Queues.Deliveries.ToList();
        factory.Queues.Deliveries.Clear();
        foreach (var item in initial) firstRound.Add(await ProcessDeliveryAsync(item.Message));
        Assert.Equal([DeliveryOutcome.Delivered, DeliveryOutcome.Retrying], firstRound.OrderBy(o => o));

        var delays = new List<TimeSpan?>();
        var outcomes = new List<DeliveryOutcome>();
        long flakyDeliveryId = 0;
        while (factory.Queues.Deliveries.TryDequeue(out var retry))
        {
            Assert.Equal(DeliveryReasons.Retry, retry.Message.Reason);
            flakyDeliveryId = retry.Message.DeliveryId;
            delays.Add(retry.Delay);
            outcomes.Add(await ProcessDeliveryAsync(retry.Message));
        }
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)], delays);
        Assert.Equal([DeliveryOutcome.Retrying, DeliveryOutcome.Retrying, DeliveryOutcome.Retrying, DeliveryOutcome.DeadLettered], outcomes);
        Assert.Empty(factory.Connector.Sent);

        var deadLettered = await client.GetFromJsonAsync<JsonElement>($"/api/v1/deliveries/{flakyDeliveryId}");
        Assert.Equal("DeadLettered", deadLettered.GetProperty("status").GetString());
        Assert.Equal(5, deadLettered.GetProperty("attemptCount").GetInt32());
        Assert.All(deadLettered.GetProperty("attempts").EnumerateArray(), a => Assert.Equal(503, a.GetProperty("httpStatus").GetInt32()));
        Assert.Contains("503 simulated outage", deadLettered.GetProperty("lastError").GetString());
        var alert = Assert.Single(factory.Alerts.Alerts);
        Assert.Contains($"Delivery {flakyDeliveryId} dead-lettered", alert.Title);

        // 5. Switch off, replay: Delivered on attempt 6, history kept.
        await SetFailureModeAsync(client, false);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/deliveries/{flakyDeliveryId}/replay", null)).StatusCode);
        var replay = factory.Queues.Deliveries.Dequeue();
        Assert.Equal(DeliveryReasons.Replay, replay.Message.Reason);
        Assert.Equal(5, replay.Message.AfterAttempt);
        Assert.Equal(DeliveryOutcome.Delivered, await ProcessDeliveryAsync(replay.Message));

        var replayed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/deliveries/{flakyDeliveryId}");
        Assert.Equal("Delivered", replayed.GetProperty("status").GetString());
        Assert.Equal(6, replayed.GetProperty("attemptCount").GetInt32());
        Assert.Equal(200, replayed.GetProperty("attempts")[5].GetProperty("httpStatus").GetInt32());
        Assert.Empty(factory.Connector.Sent);
    }

    [Fact]
    public async Task Provider_channels_are_queued_with_the_hook_shapes()
    {
        ResetFakes();
        using var client = DemoClient();

        foreach (var (channel, kind, sourceKey) in new[]
        {
            ("instagram", IncomingKinds.Meta, "meta"), ("facebook", IncomingKinds.Meta, "meta"),
            ("telegram", IncomingKinds.Telegram, DemoCatalog.TelegramSource), ("email", IncomingKinds.Email, "email"),
        })
        {
            var response = await client.PostAsJsonAsync("/api/v1/demo/simulate", new { channel });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(DemoCustomers.Pool, c => c.Name == body.GetProperty("sender").GetProperty("name").GetString()); // default sender from the pool
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("text").GetString()));                             // default text

            var msg = factory.Queues.Incoming.Dequeue();
            Assert.Equal(kind, msg.Kind);
            Assert.Equal(sourceKey, msg.SourceKey);
            Assert.Equal("", msg.KeyId); // provider hooks carry no API key; the worker finds the source by account id / source key
            using var payload = JsonDocument.Parse(msg.PayloadJson);
            Assert.Equal(JsonValueKind.Object, payload.RootElement.ValueKind);
        }
    }

    [Fact]
    public async Task Simulate_removes_the_oldest_hundred_events_once_the_cap_is_reached()
    {
        ResetFakes();
        using var client = DemoClient();

        List<long> oldestIds;
        int before;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayHubDbContext>(); // fresh scope = unscoped
            var website = await db.Sources.SingleAsync(s => s.WorkspaceId == DemoWorkspaceId && s.Name == DemoCatalog.WebsiteSource);
            var discord = await db.Destinations.SingleAsync(d => d.WorkspaceId == DemoWorkspaceId && d.Name == DemoCatalog.DiscordDestination);

            var existing = await db.Events.CountAsync(e => e.WorkspaceId == DemoWorkspaceId);
            var toInsert = Math.Max(DemoCatalog.MaxEvents - existing, 0) + 5;
            var start = DateTimeOffset.UtcNow.AddDays(-30); // older than every seeded message
            var filler = new List<Event>();
            for (var i = 0; i < toInsert; i++)
            {
                var evt = new Event
                {
                    WorkspaceId = DemoWorkspaceId, SourceId = website.Id, ExternalId = $"cap-{Guid.NewGuid():N}", Channel = "web",
                    SenderName = "Load test", Text = $"filler {i}", ReceivedAt = start.AddSeconds(i), CorrelationId = Guid.NewGuid().ToString("N"),
                };
                var delivery = new Delivery { DestinationId = discord.Id, Status = DeliveryStatus.Delivered, AttemptCount = 1 };
                delivery.Attempts.Add(new DeliveryAttempt { AttemptNo = 1, HttpStatus = 200, DurationMs = 80 });
                evt.Deliveries.Add(delivery);
                filler.Add(evt);
            }
            db.Events.AddRange(filler);
            await db.SaveChangesAsync();

            before = existing + toInsert;
            oldestIds = filler.OrderBy(e => e.ReceivedAt).Take(DemoCatalog.TrimBatch).Select(e => e.Id).ToList();
        }
        Assert.True(before >= DemoCatalog.MaxEvents);

        await SimulateWebAsync(client, "One more, please", "Linh Tran");
        Assert.Single(factory.Queues.Incoming);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayHubDbContext>();
            Assert.Equal(before - DemoCatalog.TrimBatch, await db.Events.CountAsync(e => e.WorkspaceId == DemoWorkspaceId));
            Assert.Equal(0, await db.Events.CountAsync(e => oldestIds.Contains(e.Id)));
            Assert.Equal(0, await db.Deliveries.CountAsync(d => oldestIds.Contains(d.EventId)));
            Assert.Equal(0, await db.DeliveryAttempts.CountAsync(a => !db.Deliveries.Any(d => d.Id == a.DeliveryId)));
            Assert.True(await db.Conversations.CountAsync(c => c.WorkspaceId == DemoWorkspaceId) >= 12); // conversations stay
        }
    }

    [Fact]
    public async Task Demo_endpoints_refuse_other_workspaces_and_bad_input()
    {
        ResetFakes();

        // Studio (default workspace) and unknown workspaces: 403 demo-only, nothing queued.
        using var studio = factory.CreateClient();
        var forbidden = await studio.PostAsJsonAsync("/api/v1/demo/simulate", new { channel = "web" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var problem = await forbidden.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://relayhub.dev/problems/demo-only", problem.GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await studio.GetAsync("/api/v1/demo/failure-mode")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await studio.PostAsync("/api/v1/demo/reset", null)).StatusCode);
        using var unknown = factory.CreateClient();
        unknown.DefaultRequestHeaders.Add("X-Workspace-Id", "999");
        Assert.Equal(HttpStatusCode.Forbidden, (await unknown.PostAsJsonAsync("/api/v1/demo/simulate", new { channel = "web" })).StatusCode);
        Assert.Empty(factory.Queues.Incoming);

        // Demo workspace, bad input: validation problems.
        using var client = DemoClient();
        var badChannel = await client.PostAsJsonAsync("/api/v1/demo/simulate", new { channel = "whatsapp" });
        Assert.Equal(HttpStatusCode.BadRequest, badChannel.StatusCode);
        Assert.True((await badChannel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("channel", out _));
        var tooLong = await client.PostAsJsonAsync("/api/v1/demo/simulate", new { channel = "web", text = new string('x', DemoCatalog.MaxSimulatedTextLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.True((await tooLong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("text", out _));
        Assert.Empty(factory.Queues.Incoming);
    }

    [Fact]
    public async Task Reset_restores_the_sample_data_and_turns_the_failure_switch_off()
    {
        ResetFakes();
        using var client = DemoClient();

        await SetFailureModeAsync(client, true);
        await SimulateWebAsync(client, "Extra message before reset", "Sofia Alvarez");
        Assert.Equal(IncomingOutcome.Stored, await ProcessIncomingAsync(factory.Queues.Incoming.Dequeue()));
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/v1/events?q=before%20reset")).GetProperty("total").GetInt32());

        var reset = await client.PostAsync("/api/v1/demo/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var summary = await reset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reset", summary.GetProperty("status").GetString());
        Assert.True(summary.GetProperty("conversations").GetInt32() >= 10);
        Assert.True(summary.GetProperty("events").GetInt32() > 20);
        Assert.False(summary.GetProperty("failureMode").GetBoolean());

        Assert.Equal(summary.GetProperty("events").GetInt32(), (await client.GetFromJsonAsync<JsonElement>("/api/v1/events")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/v1/events?q=before%20reset")).GetProperty("total").GetInt32());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/v1/demo/failure-mode")).GetProperty("enabled").GetBoolean());
        Assert.Equal(6, (await client.GetFromJsonAsync<JsonElement>("/api/v1/routes")).GetArrayLength()); // configuration untouched
    }
}
