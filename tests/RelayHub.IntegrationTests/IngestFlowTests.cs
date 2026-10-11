using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using Testcontainers.MsSql;

namespace RelayHub.IntegrationTests;

/// <summary>Fake Discord: records what would have been posted, and can fail on demand to exercise retry bookkeeping.</summary>
public sealed class RecordingConnector : IDestinationConnector
{
    public List<(string Destination, string Text)> Sent { get; } = [];
    public int FailNextWithStatus { get; set; }

    public DestinationType Type => DestinationType.Discord;

    public Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        if (FailNextWithStatus != 0)
        {
            var status = FailNextWithStatus;
            FailNextWithStatus = 0;
            return Task.FromResult(new DeliveryResult(false, status, $"{status} simulated failure", Retryable: status == 429 || status >= 500));
        }
        Sent.Add((destination.Name, text));
        return Task.FromResult(DeliveryResult.Ok(204));
    }
}

/// <summary>Real API + real SQL Server in Docker (Testcontainers). Connectors are faked.</summary>
public sealed class SqlServerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public RecordingConnector Connector { get; } = new();

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
        });
    }
}

[Trait("Category", "Docker")]
public class IngestFlowTests(SqlServerApiFactory factory) : IClassFixture<SqlServerApiFactory>
{
    [Fact]
    public async Task Form_message_is_stored_routed_delivered_and_deduplicated()
    {
        using var client = factory.CreateClient();

        // Configure: source -> destination -> route
        var created = await client.PostAsJsonAsync("/api/v1/sources", new { name = "contact-form" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = await created.Content.ReadFromJsonAsync<JsonElement>();
        var apiKey = source.GetProperty("apiKey").GetString()!;
        var sourceId = source.GetProperty("source").GetProperty("id").GetInt32();

        var destCreated = await client.PostAsJsonAsync("/api/v1/destinations", new
        {
            name = "Team Discord", type = "discord",
            config = new { webhookUrl = "https://discord.com/api/webhooks/123456/secret-token-value" },
        });
        Assert.Equal(HttpStatusCode.Created, destCreated.StatusCode);
        var dest = await destCreated.Content.ReadFromJsonAsync<JsonElement>();
        var destinationId = dest.GetProperty("id").GetInt32();
        Assert.DoesNotContain("secret-token-value", dest.GetProperty("config").GetRawText()); // secrets are masked

        var routeCreated = await client.PostAsJsonAsync("/api/v1/routes", new { sourceId, destinationId, template = "{{name}}: {{text}}" });
        Assert.Equal(HttpStatusCode.Created, routeCreated.StatusCode);

        // Ingest
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/contact-form")
        {
            Content = JsonContent.Create(new { name = "Anna", email = "anna@example.com", message = "Do you build websites?" }),
        };
        req.Headers.Add("X-Api-Key", apiKey);
        req.Headers.Add("Idempotency-Key", "form-0001");
        var accepted = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("accepted", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("deliveries").GetInt32());
        Assert.Single(factory.Connector.Sent);
        Assert.Equal(("Team Discord", "Anna: Do you build websites?"), factory.Connector.Sent[0]);

        // Same Idempotency-Key again -> ignored
        using var dup = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/contact-form")
        {
            Content = JsonContent.Create(new { name = "Anna", email = "anna@example.com", message = "Do you build websites?" }),
        };
        dup.Headers.Add("X-Api-Key", apiKey);
        dup.Headers.Add("Idempotency-Key", "form-0001");
        var dupResponse = await client.SendAsync(dup);
        Assert.Equal(HttpStatusCode.Accepted, dupResponse.StatusCode);
        Assert.Equal("duplicate", (await dupResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Single(factory.Connector.Sent);

        // Inbox shows one event, delivered
        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/events");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items")[0];
        Assert.Equal("web", item.GetProperty("channel").GetString());
        Assert.Equal("Delivered", item.GetProperty("deliveries")[0].GetProperty("status").GetString());

        // A failing destination is scheduled for retry with backoff
        factory.Connector.FailNextWithStatus = 503;
        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/contact-form")
        {
            Content = JsonContent.Create(new { name = "Bao", message = "Hello" }),
        };
        second.Headers.Add("X-Api-Key", apiKey);
        var secondResponse = await client.SendAsync(second);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        var eventId = (await secondResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetInt64();

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{eventId}");
        var delivery = detail.GetProperty("deliveries")[0];
        Assert.Equal("Retrying", delivery.GetProperty("status").GetString());
        Assert.Equal(1, delivery.GetProperty("attemptCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, delivery.GetProperty("nextAttemptAt").ValueKind);
        Assert.Equal(503, delivery.GetProperty("attempts")[0].GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public async Task Wrong_api_key_is_rejected_and_nothing_is_stored()
    {
        using var client = factory.CreateClient();
        var before = (await client.GetFromJsonAsync<JsonElement>("/api/v1/events")).GetProperty("total").GetInt32();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/contact-form")
        {
            Content = JsonContent.Create(new { name = "Mallory", message = "forged" }),
        };
        req.Headers.Add("X-Api-Key", "src_000000000000_00000000000000000000000000000000");
        var response = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = (await client.GetFromJsonAsync<JsonElement>("/api/v1/events")).GetProperty("total").GetInt32();
        Assert.Equal(before, after);
    }
}
