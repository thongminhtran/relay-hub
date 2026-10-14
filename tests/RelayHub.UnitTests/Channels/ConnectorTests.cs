using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Channels;
using RelayHub.Infrastructure.Connectors;

namespace RelayHub.UnitTests.Channels;

public class ConnectorTests
{
    private static readonly Event Evt = new()
    {
        Id = 42, WorkspaceId = 1, SourceId = 1, ExternalId = "m_1", Channel = "instagram", SenderName = "Anna \"the\" Lee", SenderHandle = "IGSID-7",
        Text = "line one\nline two", CorrelationId = "corr-1", ReceivedAt = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero),
    };

    private static Destination Dest(DestinationType type, object config)
        => new() { Id = 1, WorkspaceId = 1, Type = type, Name = "test", ConfigJson = JsonSerializer.Serialize(config) };

    [Fact]
    public async Task Teams_posts_an_adaptive_card_to_the_workflow_url()
    {
        var http = new StubHttp(HttpStatusCode.Accepted);
        var result = await new TeamsConnector(http.Client).SendAsync(
            Dest(DestinationType.Teams, new { webhookUrl = "https://prod-12.canadacentral.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?sig=x" }),
            "**New message** from Anna", Evt, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(202, result.HttpStatus);
        var card = http.Single.Json;
        Assert.Equal("message", card.GetProperty("type").GetString());
        var attachment = card.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var content = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", content.GetProperty("type").GetString());
        Assert.Equal("1.4", content.GetProperty("version").GetString());
        var block = content.GetProperty("body")[0];
        Assert.Equal("TextBlock", block.GetProperty("type").GetString());
        Assert.Equal("**New message** from Anna", block.GetProperty("text").GetString());
        Assert.True(block.GetProperty("wrap").GetBoolean());
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(404, false)]
    [InlineData(401, false)]
    public async Task Failures_follow_the_discord_rule_429_and_5xx_retry(int status, bool retryable)
    {
        var http = new StubHttp((HttpStatusCode)status, new { ok = false, description = "nope" });
        var result = await new TelegramConnector(http.Client).SendAsync(
            Dest(DestinationType.Telegram, new { botToken = "123:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef", chatId = "777" }), "hello", Evt, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(status, result.HttpStatus);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal("777", http.Single.Json.GetProperty("chat_id").GetString());
        Assert.Equal("https://api.telegram.org/bot123:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef/sendMessage", http.Single.Url!.ToString());
    }

    [Fact]
    public async Task Email_connector_needs_resend_settings_and_renders_the_subject_tokens()
    {
        var destination = Dest(DestinationType.Email, new { to = "owner@studio.example", subject = "[Studio] {{name}} via {{channel}}" });

        var unconfigured = await new EmailConnector(new StubHttp(HttpStatusCode.OK).Client, Options.Create(new ChannelOptions())).SendAsync(destination, "x", Evt, CancellationToken.None);
        Assert.False(unconfigured.Succeeded);
        Assert.False(unconfigured.Retryable);
        Assert.Contains("RelayHub:Resend:ApiKey", unconfigured.Error);

        var http = new StubHttp(HttpStatusCode.OK, new { id = "e1" });
        var options = Options.Create(new ChannelOptions { Resend = new ChannelOptions.ResendOptions { ApiKey = "re_x", From = "RelayHub <relay@studio.example>" } });
        var result = await new EmailConnector(http.Client, options).SendAsync(destination, "**New message from Anna**\nline one", Evt, CancellationToken.None);

        Assert.True(result.Succeeded);
        var call = http.Single;
        Assert.Equal("Bearer re_x", call.Authorization);
        Assert.Equal("RelayHub <relay@studio.example>", call.Json.GetProperty("from").GetString());
        Assert.Equal("owner@studio.example", call.Json.GetProperty("to")[0].GetString());
        Assert.Equal("[Studio] Anna \"the\" Lee via instagram", call.Json.GetProperty("subject").GetString());
        Assert.Equal("**New message from Anna**\nline one", call.Json.GetProperty("text").GetString());
    }

    [Fact]
    public void Http_body_template_values_are_json_escaped_when_the_template_is_json()
    {
        var (body, contentType) = HttpConnector.RenderBody("""{"who":"{{name}}","msg":"{{text}}","id":{{eventId}}}""", "line one\nline \"two\"", Evt);

        Assert.Equal("application/json", contentType);
        using var doc = JsonDocument.Parse(body); // would throw if the quotes / newline had broken the document
        Assert.Equal("Anna \"the\" Lee", doc.RootElement.GetProperty("who").GetString());
        Assert.Equal("line one\nline \"two\"", doc.RootElement.GetProperty("msg").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());

        var (plain, plainType) = HttpConnector.RenderBody("Hello {{name}}: {{text}}", "say \"hi\"", Evt);
        Assert.Equal("text/plain", plainType);
        Assert.Equal("Hello Anna \"the\" Lee: say \"hi\"", plain);

        var (summary, summaryType) = HttpConnector.RenderBody(null, "rendered", Evt);
        Assert.Equal("application/json", summaryType);
        using var s = JsonDocument.Parse(summary);
        Assert.Equal(42, s.RootElement.GetProperty("eventId").GetInt64());
        Assert.Equal("rendered", s.RootElement.GetProperty("text").GetString());
        Assert.Equal("corr-1", s.RootElement.GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.0.0.8/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[fe80::1]/hook")]
    [InlineData("http://93.184.216.34/hook")]
    [InlineData("https://localhost:5082/hook")]
    public async Task Http_connector_refuses_private_targets_before_sending_and_does_not_retry_them(string url)
    {
        var http = new StubHttp(HttpStatusCode.OK);
        var result = await new HttpConnector(http.Client).SendAsync(Dest(DestinationType.Http, new { url, method = "POST" }), "x", Evt, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("SSRF", result.Error);
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task Http_connector_sends_to_a_public_https_target_with_custom_headers_and_refuses_redirects()
    {
        var http = new StubHttp(HttpStatusCode.OK);
        var destination = Dest(DestinationType.Http, new
        {
            url = "https://93.184.216.34/relay", method = "put",
            headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc", ["X-Custom"] = "1", ["Host"] = "evil.example", ["Content-Type"] = "application/vnd.api+json" },
            bodyTemplate = """{"text":"{{text}}"}""",
        });

        var result = await new HttpConnector(http.Client).SendAsync(destination, "hello", Evt, CancellationToken.None);

        Assert.True(result.Succeeded);
        var call = http.Single;
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal("https://93.184.216.34/relay", call.Url!.ToString());
        Assert.Equal("Bearer abc", call.Authorization);
        Assert.Equal("1", call.Headers["X-Custom"]);
        Assert.False(call.Headers.ContainsKey("Host"));
        Assert.Equal("application/vnd.api+json", call.ContentType);
        Assert.Equal("hello", call.Json.GetProperty("text").GetString());

        var redirecting = new StubHttp(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://169.254.169.254/") } });
        var redirect = await new HttpConnector(redirecting.Client).SendAsync(destination, "hello", Evt, CancellationToken.None);
        Assert.False(redirect.Succeeded);
        Assert.False(redirect.Retryable);
        Assert.Contains("redirects are not followed", redirect.Error);
    }
}
