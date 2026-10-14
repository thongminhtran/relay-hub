using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RelayHub.Core.Conversations;
using RelayHub.Core.Domain;

namespace RelayHub.IntegrationTests;

[Trait("Category", "Docker")]
[Collection(SqlServerCollection.Name)]
public class InboxTests(SqlServerApiFactory factory)
{
    [Fact]
    public async Task Conversations_list_and_detail_show_the_thread_and_the_reply_window()
    {
        var demo = await factory.DemoWorkspaceAsync();
        var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var (source, conversation, inbound) = await factory.SeedConversationAsync(demo.Id, SourceType.Instagram, "demo-instagram", "ig-user-1001", "Linh Nguyen", "@linh.makes", oneHourAgo, "Is the studio open on Saturday?");
        using var client = await factory.CreateDemoClientAsync();

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/conversations?channel=instagram&q=Saturday");
        Assert.True(list.GetProperty("total").GetInt32() >= 1);
        var item = list.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == conversation.Id);
        Assert.Equal("instagram", item.GetProperty("channel").GetString());
        Assert.Equal(source.Id, item.GetProperty("sourceId").GetInt32());
        Assert.Equal("demo-instagram", item.GetProperty("sourceName").GetString());
        Assert.Equal("Linh Nguyen", item.GetProperty("senderName").GetString());
        Assert.Equal("@linh.makes", item.GetProperty("senderHandle").GetString());
        Assert.Equal("Is the studio open on Saturday?", item.GetProperty("lastText").GetString());
        Assert.Equal("in", item.GetProperty("lastDirection").GetString());
        Assert.Equal(1, item.GetProperty("messageCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("lastOutboundAt").ValueKind);
        Assert.Equal(oneHourAgo, item.GetProperty("lastMessageAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));

        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/v1/conversations?channel=telegram&q=Saturday")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/conversations?channel=fax")).StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation.Id}");
        Assert.Equal("ig-user-1001", detail.GetProperty("externalThreadId").GetString());
        var reply = detail.GetProperty("reply");
        Assert.True(reply.GetProperty("canReply").GetBoolean());
        Assert.Equal(oneHourAgo + ReplyWindow.MetaWindow, reply.GetProperty("windowClosesAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("reason").ValueKind);
        var message = Assert.Single(detail.GetProperty("messages").EnumerateArray());
        Assert.Equal(inbound.Id, message.GetProperty("id").GetInt64());
        Assert.Equal("in", message.GetProperty("direction").GetString());
        Assert.Equal(inbound.CorrelationId, message.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Reply_in_the_demo_workspace_is_mocked_stored_and_pushed()
    {
        var demo = await factory.DemoWorkspaceAsync();
        var (_, conversation, _) = await factory.SeedConversationAsync(demo.Id, SourceType.Facebook, "demo-facebook", "psid-2002", "Minh Tran", null, DateTimeOffset.UtcNow.AddMinutes(-5), "Do you take walk-ins?");
        using var client = await factory.CreateDemoClientAsync();
        factory.Notifier.RepliesSent.Clear();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/conversations/{conversation.Id}/reply", new { text = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/conversations/{conversation.Id}/reply", new { text = new string('x', 2001) })).StatusCode);

        var response = await client.PostAsJsonAsync($"/api/v1/conversations/{conversation.Id}/reply", new { text = "Yes, every day from 10 to 6!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SentDemo", body.GetProperty("status").GetString());
        Assert.Equal("facebook", body.GetProperty("channel").GetString());
        Assert.Equal(conversation.Id, body.GetProperty("conversationId").GetInt32());
        Assert.StartsWith("demo-", body.GetProperty("providerMessageId").GetString());
        var messageId = body.GetProperty("messageId").GetInt64();

        // Stored as an outbound event (no deliveries), the thread shows it last, and the dashboard was told.
        var pushed = Assert.Single(factory.Notifier.RepliesSent);
        Assert.Equal((messageId, demo.Id, "Yes, every day from 10 to 6!"), pushed);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation.Id}");
        var messages = detail.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("out", messages[1].GetProperty("direction").GetString());
        Assert.Equal(messageId, messages[1].GetProperty("id").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("lastOutboundAt").ValueKind);

        var evt = await client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{messageId}");
        Assert.Equal("out", evt.GetProperty("direction").GetString());
        Assert.Equal("You", evt.GetProperty("from").GetProperty("name").GetString());
        Assert.Empty(evt.GetProperty("deliveries").EnumerateArray());

        var outbound = await client.GetFromJsonAsync<JsonElement>("/api/v1/events?direction=out&q=walk");
        Assert.Equal(0, outbound.GetProperty("total").GetInt32()); // the question was inbound
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/conversations?q=walk-ins");
        var item = listed.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == conversation.Id);
        Assert.Equal("out", item.GetProperty("lastDirection").GetString());
        Assert.Equal(2, item.GetProperty("messageCount").GetInt32());
    }

    [Fact]
    public async Task Reply_after_the_meta_window_is_409_with_the_contract_problem()
    {
        var demo = await factory.DemoWorkspaceAsync();
        var lastInbound = DateTimeOffset.UtcNow.AddHours(-25);
        var (_, conversation, _) = await factory.SeedConversationAsync(demo.Id, SourceType.Instagram, "demo-instagram", "ig-user-3003", "Old Thread", "@old", lastInbound, "Hello from yesterday");
        using var client = await factory.CreateDemoClientAsync();

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation.Id}");
        var window = detail.GetProperty("reply");
        Assert.False(window.GetProperty("canReply").GetBoolean());
        Assert.Equal(lastInbound + ReplyWindow.MetaWindow, window.GetProperty("windowClosesAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.Equal(ReplyWindow.MetaWindowClosedReason, window.GetProperty("reason").GetString());

        var response = await client.PostAsJsonAsync($"/api/v1/conversations/{conversation.Id}/reply", new { text = "Too late?" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://relayhub.dev/problems/reply-window-closed", problem.GetProperty("type").GetString());
        Assert.Equal("Reply window closed", problem.GetProperty("title").GetString());
        Assert.StartsWith(ReplyWindow.MetaWindowClosedReason + " (last message: ", problem.GetProperty("detail").GetString());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation.Id}")).GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Reply_without_a_registered_channel_is_501_and_web_without_email_has_no_channel()
    {
        var studio = await factory.StudioWorkspaceAsync();
        using var owner = await factory.CreateOwnerClientAsync();

        // Telegram conversation in Studio: the window is open, but this branch registers no IReplyChannel -> 501 reply-unsupported.
        var (_, telegram, _) = await factory.SeedConversationAsync(studio.Id, SourceType.Telegram, "studio-telegram", "chat-4004", "Bot User", "@botuser", DateTimeOffset.UtcNow.AddMinutes(-1), "ping");
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{telegram.Id}");
        Assert.True(detail.GetProperty("reply").GetProperty("canReply").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("reply").GetProperty("windowClosesAt").ValueKind);

        var response = await owner.PostAsJsonAsync($"/api/v1/conversations/{telegram.Id}/reply", new { text = "pong" });
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://relayhub.dev/problems/reply-unsupported", problem.GetProperty("type").GetString());
        Assert.Equal("No reply channel for telegram", problem.GetProperty("title").GetString());

        // Web conversation without an email: no reply channel at all.
        var (_, web, _) = await factory.SeedConversationAsync(studio.Id, SourceType.Generic, "studio-web", "anonymous visitor", "Anonymous", null, DateTimeOffset.UtcNow, "no email left");
        var webDetail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{web.Id}");
        Assert.False(webDetail.GetProperty("reply").GetProperty("canReply").GetBoolean());
        Assert.Equal(ReplyWindow.NoReplyChannelReason, webDetail.GetProperty("reply").GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.NotImplemented, (await owner.PostAsJsonAsync($"/api/v1/conversations/{web.Id}/reply", new { text = "hello?" })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/v1/conversations/999999/reply", new { text = "ghost" })).StatusCode);
    }

    [Fact]
    public async Task Stats_summary_has_the_contract_shape()
    {
        var demo = await factory.DemoWorkspaceAsync();
        await factory.SeedConversationAsync(demo.Id, SourceType.Email, "demo-email", "someone@example.com", "Someone", "someone@example.com", DateTimeOffset.UtcNow.AddMinutes(-2), "stats seed");
        using var client = await factory.CreateDemoClientAsync();

        var stats = await client.GetFromJsonAsync<JsonElement>("/api/v1/stats/summary");

        var today = stats.GetProperty("today");
        foreach (var field in new[] { "events", "replies", "delivered", "retrying", "deadLettered" }) Assert.Equal(JsonValueKind.Number, today.GetProperty(field).ValueKind);
        Assert.True(today.GetProperty("events").GetInt32() >= 1);

        var total = stats.GetProperty("total");
        foreach (var field in new[] { "events", "conversations", "delivered", "deadLettered" }) Assert.Equal(JsonValueKind.Number, total.GetProperty(field).ValueKind);
        Assert.True(total.GetProperty("conversations").GetInt32() >= 1);
        Assert.True(total.GetProperty("events").GetInt32() >= today.GetProperty("events").GetInt32());

        Assert.Contains(stats.GetProperty("successRate").ValueKind, new[] { JsonValueKind.Null, JsonValueKind.Number });

        var byChannel = stats.GetProperty("byChannel").EnumerateArray().ToList();
        Assert.Contains(byChannel, c => c.GetProperty("channel").GetString() == "email" && c.GetProperty("count").GetInt32() >= 1);
        Assert.True(byChannel.Zip(byChannel.Skip(1)).All(p => p.First.GetProperty("count").GetInt32() >= p.Second.GetProperty("count").GetInt32()));

        var series = stats.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(7, series.Count);
        Assert.Equal(DateTime.UtcNow.Date.ToString("yyyy-MM-dd"), series[^1].GetProperty("date").GetString());
        Assert.Equal(DateTime.UtcNow.Date.AddDays(-6).ToString("yyyy-MM-dd"), series[0].GetProperty("date").GetString());
        foreach (var point in series) foreach (var field in new[] { "events", "delivered", "failed" }) Assert.Equal(JsonValueKind.Number, point.GetProperty(field).ValueKind);
        Assert.True(series[^1].GetProperty("events").GetInt32() >= 1);

        // Studio does not see demo numbers.
        using var owner = await factory.CreateOwnerClientAsync();
        var ownerStats = await owner.GetFromJsonAsync<JsonElement>("/api/v1/stats/summary");
        Assert.DoesNotContain(ownerStats.GetProperty("byChannel").EnumerateArray(), c => c.GetProperty("channel").GetString() == "email");
    }

    [Fact]
    public async Task Realtime_negotiate_is_503_when_signalr_is_not_configured()
    {
        using var client = await factory.CreateDemoClientAsync();

        var response = await client.PostAsync("/api/v1/realtime/negotiate", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://relayhub.dev/problems/realtime-unavailable", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Destination_test_is_mocked_for_demo_users()
    {
        var demo = await factory.DemoWorkspaceAsync();
        var destinationId = await factory.WithDbAsync(async db =>
        {
            var d = new Destination { WorkspaceId = demo.Id, Type = DestinationType.Discord, Name = "Demo Discord", ConfigJson = """{"webhookUrl":"https://discord.com/api/webhooks/1/demo"}""" };
            db.Destinations.Add(d);
            await db.SaveChangesAsync();
            return d.Id;
        });
        using var client = await factory.CreateDemoClientAsync();
        var sentBefore = factory.Connector.Sent.Count;

        var response = await client.PostAsync($"/api/v1/destinations/{destinationId}/test", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("succeeded").GetBoolean());
        Assert.True(body.GetProperty("mocked").GetBoolean());
        Assert.Equal(sentBefore, factory.Connector.Sent.Count); // nothing left the system
    }
}
