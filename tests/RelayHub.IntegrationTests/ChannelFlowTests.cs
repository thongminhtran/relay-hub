using System.Net;
using System.Net.Http.Json;
using System.Text;
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
using RelayHub.Core.Security;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Processing;
using Testcontainers.MsSql;

namespace RelayHub.IntegrationTests;

/// <summary>
/// Same recipe as <see cref="SqlServerApiFactory"/> (real API + real SQL Server in Docker, faked queues / connectors / alerts) plus the
/// channel secrets. Separate class so the Week 4 tests do not depend on the REST configuration endpoints or their auth: sources,
/// destinations and routes are written straight through the DbContext and assertions read it back.
/// </summary>
public sealed class ChannelApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string MetaAppSecret = "meta-app-secret-integration";
    public const string MetaVerifyToken = "meta-verify-token-integration";
    public const string TelegramSecret = "telegram-secret-token-integration";
    public const string EmailSecret = "email-inbound-secret-integration";

    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public RecordingConnector Connector { get; } = new();
    public InMemoryQueues Queues { get; } = new();
    public RecordingAlerts Alerts { get; } = new();

    public Task InitializeAsync() => _sql.StartAsync();

    private string? _ownerToken, _demoToken;

    /// <summary>Owner client (dev owner from the seeder, Studio workspace); the token is fetched once per factory.</summary>
    public async Task<HttpClient> CreateOwnerClientAsync()
    {
        if (_ownerToken is null)
        {
            using var anonymous = CreateClient();
            var response = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email = DevSeeder.DevOwnerEmail, password = DevSeeder.DevOwnerPassword });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ownerToken = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }
        return SqlServerApiFactory.WithBearer(CreateClient(), _ownerToken);
    }

    /// <summary>Anonymous Demo token (Demo workspace, role Demo).</summary>
    public async Task<HttpClient> CreateDemoClientAsync()
    {
        if (_demoToken is null)
        {
            using var anonymous = CreateClient();
            var response = await anonymous.PostAsJsonAsync("/api/v1/auth/demo", new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _demoToken = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }
        return SqlServerApiFactory.WithBearer(CreateClient(), _demoToken);
    }

    Task IAsyncLifetime.DisposeAsync() => _sql.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:RelayHub", _sql.GetConnectionString());
        builder.UseSetting("RelayHub:ApiKeyMasterKey", "integration-test-master-key");
        builder.UseSetting("RelayHub:MigrateOnStartup", "true");
        builder.UseSetting("RelayHub:SeedOnStartup", "true");
        builder.UseSetting("RelayHub:Meta:AppSecret", MetaAppSecret);
        builder.UseSetting("RelayHub:Meta:VerifyToken", MetaVerifyToken);
        builder.UseSetting("RelayHub:Telegram:WebhookSecret", TelegramSecret);
        builder.UseSetting("RelayHub:Email:InboundSecret", EmailSecret);
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

    /// <summary>Runs <paramref name="work"/> against an unscoped DbContext (all workspaces visible), the way the seeder and the worker do.</summary>
    public async Task<T> WithDbAsync<T>(Func<RelayHubDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AmbientWorkspace>().WorkspaceId = null;
        return await work(scope.ServiceProvider.GetRequiredService<RelayHubDbContext>());
    }

    /// <summary>A source of the given type in the Studio workspace, with a Discord destination and a route so deliveries are produced.</summary>
    public Task<Source> CreateSourceWithRouteAsync(SourceType type, string name, string? externalAccountId, string configJson, string template = "{{name}}: {{text}}")
        => WithDbAsync(async db =>
        {
            var studio = await db.Workspaces.SingleAsync(w => w.Name == "Studio");
            var source = new Source { WorkspaceId = studio.Id, Type = type, Name = name, KeyId = ApiKeys.NewKeyId(), ExternalAccountId = externalAccountId, ConfigJson = configJson };
            var destination = new Destination { WorkspaceId = studio.Id, Type = DestinationType.Discord, Name = $"Discord for {name}", ConfigJson = """{"webhookUrl":"https://discord.com/api/webhooks/1/x"}""" };
            db.Sources.Add(source);
            db.Destinations.Add(destination);
            await db.SaveChangesAsync();
            db.Routes.Add(new RelayHub.Core.Domain.Route { WorkspaceId = studio.Id, SourceId = source.Id, DestinationId = destination.Id, Template = template });
            await db.SaveChangesAsync();
            return source;
        });

    /// <summary>What the Functions worker would do: drain both queues, one scope per message.</summary>
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
public class ChannelFlowTests(ChannelApiFactory factory) : IClassFixture<ChannelApiFactory>
{
    private static HttpRequestMessage Signed(string path, string headerName, string secret, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(headerName, WebhookSignatures.Sign(bytes, secret));
        return request;
    }

    private static string MetaBatch(string obj, string accountId, string senderId, string mid, string text) => JsonSerializer.Serialize(new
    {
        @object = obj,
        entry = new[]
        {
            new
            {
                id = accountId, time = 1760443200000L,
                messaging = new[] { new { sender = new { id = senderId }, recipient = new { id = accountId }, timestamp = 1760443200123L, message = new { mid, text } } },
            },
        },
    });

    [Fact]
    public async Task Meta_verification_handshake_echoes_the_challenge_only_for_the_right_token()
    {
        using var client = factory.CreateClient();

        var ok = await client.GetAsync($"/api/v1/hooks/meta?hub.mode=subscribe&hub.verify_token={ChannelApiFactory.MetaVerifyToken}&hub.challenge=1158201444");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.StartsWith("text/plain", ok.Content.Headers.ContentType?.ToString());
        Assert.Equal("1158201444", await ok.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/hooks/meta?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/hooks/meta?hub.mode=unsubscribe&hub.verify_token={ChannelApiFactory.MetaVerifyToken}&hub.challenge=1")).StatusCode);
    }

    [Fact]
    public async Task Instagram_message_is_signed_queued_parsed_stored_routed_and_deduplicated_on_mid()
    {
        using var client = factory.CreateClient();
        var source = await factory.CreateSourceWithRouteAsync(SourceType.Instagram, "studio-ig", "17841400000001", """{"pageAccessToken":"EAAB-test"}""");
        factory.Connector.Sent.Clear();
        factory.Queues.Incoming.Clear();

        var body = MetaBatch("instagram", "17841400000001", "IGSID-7", "m_ig_0001", "Is the studio open Saturday?");
        var accepted = await client.SendAsync(Signed("/api/v1/hooks/meta", "X-Hub-Signature-256", ChannelApiFactory.MetaAppSecret, body));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode); // Meta insists on 200
        var queued = Assert.Single(factory.Queues.Incoming);
        Assert.Equal(IncomingKinds.Meta, queued.Kind);
        Assert.Equal("meta", queued.SourceKey);
        Assert.Equal("", queued.KeyId);
        Assert.Equal(32, queued.ExternalId.Length);

        var run = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Stored], run.Incoming);
        Assert.Equal([DeliveryOutcome.Delivered], run.Deliveries);
        Assert.Equal(("Discord for studio-ig", "Instagram user: Is the studio open Saturday?"), Assert.Single(factory.Connector.Sent));

        var (evt, conversation) = await factory.WithDbAsync(async db =>
        {
            var e = await db.Events.SingleAsync(x => x.SourceId == source.Id);
            var c = await db.Conversations.SingleAsync(x => x.Id == e.ConversationId);
            return (e, c);
        });
        Assert.Equal("instagram", evt.Channel);
        Assert.Equal("m_ig_0001", evt.ExternalId);
        Assert.Equal("m_ig_0001", evt.ProviderMessageId);
        Assert.Equal("IGSID-7", evt.SenderHandle);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760443200123), evt.ReceivedAt);
        Assert.Equal("IGSID-7", conversation.ExternalThreadId);
        Assert.Equal(evt.ReceivedAt, conversation.LastInboundAt);
        Assert.Equal(source.WorkspaceId, conversation.WorkspaceId);

        // Meta redelivers the same batch (or the queue redelivers the message): same mid → nothing new.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed("/api/v1/hooks/meta", "X-Hub-Signature-256", ChannelApiFactory.MetaAppSecret, body))).StatusCode);
        var again = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Duplicate], again.Incoming);
        Assert.Empty(again.Deliveries);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Events.CountAsync(x => x.SourceId == source.Id)));

        // A second message in the same thread lands in the same conversation.
        var second = MetaBatch("instagram", "17841400000001", "IGSID-7", "m_ig_0002", "Around noon?");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed("/api/v1/hooks/meta", "X-Hub-Signature-256", ChannelApiFactory.MetaAppSecret, second))).StatusCode);
        Assert.Equal([IncomingOutcome.Stored], (await factory.RunWorkerAsync()).Incoming);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Conversations.CountAsync(x => x.SourceId == source.Id)));
        Assert.Equal(2, await factory.WithDbAsync(db => db.Events.CountAsync(x => x.ConversationId == conversation.Id)));
    }

    [Fact]
    public async Task Facebook_page_message_uses_the_page_source_and_channel_facebook()
    {
        using var client = factory.CreateClient();
        var source = await factory.CreateSourceWithRouteAsync(SourceType.Facebook, "studio-page", "100200300", """{"pageAccessToken":"EAAB-test"}""");
        factory.Queues.Incoming.Clear();

        var body = MetaBatch("page", "100200300", "PSID-42", "m_fb_0001", "Do you do logo design?");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed("/api/v1/hooks/meta", "X-Hub-Signature-256", ChannelApiFactory.MetaAppSecret, body))).StatusCode);
        Assert.Equal([IncomingOutcome.Stored], (await factory.RunWorkerAsync()).Incoming);

        var evt = await factory.WithDbAsync(db => db.Events.SingleAsync(x => x.SourceId == source.Id));
        Assert.Equal("facebook", evt.Channel);
        Assert.Equal("Do you do logo design?", evt.Text);
        Assert.Equal("Facebook user", evt.SenderName);
    }

    [Fact]
    public async Task Meta_wrong_signature_is_401_and_nothing_is_queued_unknown_page_is_dropped_by_the_worker()
    {
        using var client = factory.CreateClient();
        factory.Queues.Incoming.Clear();
        var body = MetaBatch("page", "100200300", "PSID-1", "m_x", "hi");

        var forged = await client.SendAsync(Signed("/api/v1/hooks/meta", "X-Hub-Signature-256", "not-the-app-secret", body));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Empty(factory.Queues.Incoming);

        using var unsigned = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/meta") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unsigned)).StatusCode);

        // Signature valid but the body was altered after signing → 401.
        var tampered = Signed("/api/v1/hooks/meta", "X-Hub-Signature-256", ChannelApiFactory.MetaAppSecret, body);
        tampered.Content = new StringContent(body.Replace("hi", "ho"), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(tampered)).StatusCode);
        Assert.Empty(factory.Queues.Incoming);

        // Properly signed batch for a Page nobody connected: accepted at the edge (no SQL there), dropped by the worker, no event.
        var unknown = MetaBatch("page", "999999", "PSID-1", "m_unknown", "hello?");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed("/api/v1/hooks/meta", "X-Hub-Signature-256", ChannelApiFactory.MetaAppSecret, unknown))).StatusCode);
        var run = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Dropped], run.Incoming);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Events.CountAsync(e => e.ExternalId == "m_unknown")));

        // A batch of read receipts / echoes for a connected Page is valid but carries nothing: Ignored, not Dropped, no event.
        await factory.CreateSourceWithRouteAsync(SourceType.Facebook, "receipts-page", "100200301", """{"pageAccessToken":"x"}""");
        const string receipts = """
            {"object":"page","entry":[{"id":"100200301","messaging":[
              {"sender":{"id":"PSID-1"},"recipient":{"id":"100200301"},"timestamp":1,"read":{"watermark":1}},
              {"sender":{"id":"100200301"},"recipient":{"id":"PSID-1"},"timestamp":2,"message":{"mid":"m_echo","is_echo":true,"text":"our reply"}}]}]}
            """;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed("/api/v1/hooks/meta", "X-Hub-Signature-256", ChannelApiFactory.MetaAppSecret, receipts))).StatusCode);
        Assert.Equal([IncomingOutcome.Ignored], (await factory.RunWorkerAsync()).Incoming);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Events.CountAsync(e => e.ExternalId == "m_echo")));
    }

    [Fact]
    public async Task Telegram_update_with_the_secret_token_becomes_a_telegram_event_in_the_chat_conversation()
    {
        using var client = factory.CreateClient();
        var source = await factory.CreateSourceWithRouteAsync(SourceType.Telegram, "studio-bot", null, """{"botToken":"123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw"}""");
        factory.Connector.Sent.Clear();
        factory.Queues.Incoming.Clear();

        const string update = """
            {"update_id":900001,"message":{"message_id":55,"from":{"id":777,"is_bot":false,"first_name":"Linh","last_name":"Nguyen","username":"linhng"},
             "chat":{"id":777,"first_name":"Linh","type":"private"},"date":1760443200,"text":"Còn chỗ tuần sau không?"}}
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/telegram/studio-bot") { Content = new StringContent(update, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", ChannelApiFactory.TelegramSecret);
        var accepted = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var queued = Assert.Single(factory.Queues.Incoming);
        Assert.Equal(IncomingKinds.Telegram, queued.Kind);
        Assert.Equal("studio-bot", queued.SourceKey);
        Assert.Equal("tg:900001", queued.ExternalId);

        var run = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Stored], run.Incoming);
        Assert.Equal([DeliveryOutcome.Delivered], run.Deliveries);
        Assert.Equal("Linh Nguyen: Còn chỗ tuần sau không?", Assert.Single(factory.Connector.Sent).Text);

        var (evt, conversation) = await factory.WithDbAsync(async db =>
        {
            var e = await db.Events.SingleAsync(x => x.SourceId == source.Id);
            return (e, await db.Conversations.SingleAsync(x => x.Id == e.ConversationId));
        });
        Assert.Equal("telegram", evt.Channel);
        Assert.Equal("tg:900001", evt.ExternalId);
        Assert.Equal("777", conversation.ExternalThreadId);
        Assert.Equal("Linh Nguyen", conversation.SenderName);
        Assert.Equal("@linhng", conversation.SenderHandle);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760443200), evt.ReceivedAt);

        // Wrong secret → 401, nothing queued. Unknown source key → accepted, dropped by the worker.
        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/telegram/studio-bot") { Content = new StringContent(update, Encoding.UTF8, "application/json") };
        wrong.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
        Assert.Empty(factory.Queues.Incoming);

        using var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/telegram/no-such-bot") { Content = new StringContent(update.Replace("900001", "900002"), Encoding.UTF8, "application/json") };
        unknown.Headers.Add("X-Telegram-Bot-Api-Secret-Token", ChannelApiFactory.TelegramSecret);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(unknown)).StatusCode);
        Assert.Equal([IncomingOutcome.Dropped], (await factory.RunWorkerAsync()).Incoming);
    }

    [Fact]
    public async Task Inbound_email_from_the_cloudflare_worker_is_verified_queued_and_stored_with_subject_and_body()
    {
        using var client = factory.CreateClient();
        var source = await factory.CreateSourceWithRouteAsync(SourceType.Email, "studio-inbox", "hello@studio.example", """{"fromAddress":null}""");
        factory.Connector.Sent.Clear();
        factory.Queues.Incoming.Clear();

        const string mail = """
            {"to":"Studio <Hello@Studio.Example>","from":"Anna Lee <anna@example.com>","fromName":"Anna Lee","subject":"Quote for a brand kit",
             "text":"Hi,\n\nCould you send a quote?","html":"<p>Hi,</p><p>Could you send a quote?</p>","messageId":"<CAF+abc123@mail.example.com>",
             "inReplyTo":null,"receivedAt":"2026-10-14T11:58:00Z"}
            """;
        var accepted = await client.SendAsync(Signed("/api/v1/hooks/email", "X-RelayHub-Signature", ChannelApiFactory.EmailSecret, mail));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var queued = Assert.Single(factory.Queues.Incoming);
        Assert.Equal(IncomingKinds.Email, queued.Kind);
        Assert.Equal("email", queued.SourceKey);
        Assert.Equal("<CAF+abc123@mail.example.com>", queued.ExternalId);

        var run = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Stored], run.Incoming);
        Assert.Equal([DeliveryOutcome.Delivered], run.Deliveries);
        Assert.Equal("Anna Lee: Quote for a brand kit\n\nHi,\n\nCould you send a quote?", Assert.Single(factory.Connector.Sent).Text);

        var (evt, conversation) = await factory.WithDbAsync(async db =>
        {
            var e = await db.Events.SingleAsync(x => x.SourceId == source.Id);
            return (e, await db.Conversations.SingleAsync(x => x.Id == e.ConversationId));
        });
        Assert.Equal("email", evt.Channel);
        Assert.Equal("anna@example.com", conversation.ExternalThreadId);
        Assert.Equal("anna@example.com", conversation.SenderHandle);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 11, 58, 0, TimeSpan.Zero), evt.ReceivedAt);

        // Same Message-ID again (Cloudflare retry) → duplicate.
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Signed("/api/v1/hooks/email", "X-RelayHub-Signature", ChannelApiFactory.EmailSecret, mail))).StatusCode);
        Assert.Equal([IncomingOutcome.Duplicate], (await factory.RunWorkerAsync()).Incoming);

        // Wrong signature → 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Signed("/api/v1/hooks/email", "X-RelayHub-Signature", "wrong", mail))).StatusCode);
        Assert.Empty(factory.Queues.Incoming);
    }

    [Fact]
    public async Task Generic_form_path_still_works_after_the_parser_refactor()
    {
        using var client = factory.CreateClient();
        var source = await factory.CreateSourceWithRouteAsync(SourceType.Generic, "contact-form-w4", null, "{}");
        factory.Connector.Sent.Clear();
        factory.Queues.Incoming.Clear();
        var apiKey = ApiKeys.Create(source.KeyId, "integration-test-master-key");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hooks/contact-form-w4") { Content = JsonContent.Create(new { name = "Anna", email = "anna@example.com", message = "Do you build websites?" }) };
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Add("Idempotency-Key", "form-w4-0001");
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(request)).StatusCode);

        var run = await factory.RunWorkerAsync();
        Assert.Equal([IncomingOutcome.Stored], run.Incoming);
        Assert.Equal("Anna: Do you build websites?", Assert.Single(factory.Connector.Sent).Text);
        var evt = await factory.WithDbAsync(db => db.Events.SingleAsync(e => e.SourceId == source.Id));
        Assert.Equal("web", evt.Channel);
        Assert.Equal("form-w4-0001", evt.ExternalId);
        Assert.Null(evt.ProviderMessageId);
        var conversation = await factory.WithDbAsync(db => db.Conversations.SingleAsync(c => c.Id == evt.ConversationId));
        Assert.Equal("anna@example.com", conversation.ExternalThreadId);
    }

    /// <summary>
    /// The only test here that goes through the REST configuration API (the SSRF check and the demo refusal live in the endpoint).
    /// After JWT auth lands it needs an Owner bearer token and, for the demo part, a Demo token instead of the X-Workspace-Id header.
    /// </summary>
    [Fact]
    public async Task Http_destination_is_validated_against_the_ssrf_guard_and_refused_in_the_demo_workspace()
    {
        using var client = await factory.CreateOwnerClientAsync();

        var privateTarget = await client.PostAsJsonAsync("/api/v1/destinations", new { name = "metadata", type = "http", config = new { url = "https://169.254.169.254/latest/meta-data/", method = "POST" } });
        Assert.Equal(HttpStatusCode.BadRequest, privateTarget.StatusCode);
        Assert.Contains("private", await privateTarget.Content.ReadAsStringAsync());

        var http = await client.PostAsJsonAsync("/api/v1/destinations", new { name = "plain", type = "http", config = new { url = "http://93.184.216.34/hook" } });
        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/v1/destinations", new
        {
            name = "my-system", type = "http",
            config = new { url = "https://93.184.216.34/hook", method = "put", headers = new { Authorization = "Bearer abc" }, bodyTemplate = """{"text":"{{text}}"}""" },
        });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var dto = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PUT", dto.GetProperty("config").GetProperty("method").GetString());

        // Demo workspace: HTTP destinations are owner-only, and Demo tokens cannot create destinations at all (403 from the Owner policy).
        using var demo = await factory.CreateDemoClientAsync();
        var forbidden = await demo.PostAsJsonAsync("/api/v1/destinations", new { name = "demo-http", type = "http", config = new { url = "https://93.184.216.34/hook" } });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // The other new types validate their configs too.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/destinations", new { name = "teams", type = "teams", config = new { webhookUrl = "https://prod-12.canadacentral.logic.azure.com:443/workflows/x/triggers/manual/paths/invoke?sig=y" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/destinations", new { name = "teams-bad", type = "teams", config = new { webhookUrl = "https://example.com/hook" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/destinations", new { name = "mail", type = "email", config = new { to = "owner@studio.example", subject = "[Studio] {{name}}" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/destinations", new { name = "mail-bad", type = "email", config = new { to = "not-an-email" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/destinations", new { name = "tg", type = "telegram", config = new { botToken = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw", chatId = -100123 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/destinations", new { name = "tg-bad", type = "telegram", config = new { botToken = "nope", chatId = "777" } })).StatusCode);
    }
}
