using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Channels;
using RelayHub.Infrastructure.Replies;

namespace RelayHub.UnitTests.Channels;

public class ReplyChannelTests
{
    private static readonly Source IgSource = new()
    {
        Id = 1, WorkspaceId = 1, Type = SourceType.Instagram, Name = "studio-ig", KeyId = "k", ExternalAccountId = "17841400000001",
        ConfigJson = """{"pageAccessToken":"EAAB-page-token"}""",
    };

    private static readonly Conversation IgConversation = new() { Id = 5, WorkspaceId = 1, SourceId = 1, ExternalThreadId = "IGSID-7", SenderName = "Instagram user" };

    [Fact]
    public async Task Meta_reply_posts_to_the_account_node_with_bearer_token_and_returns_the_message_id()
    {
        var http = new StubHttp(HttpStatusCode.OK, new { recipient_id = "IGSID-7", message_id = "m_reply_1" });
        var channel = new MetaReplyChannel(http.Client, SourceType.Instagram, NullLogger<MetaReplyChannel>.Instance);

        var result = await channel.SendAsync(IgSource, IgConversation, "Yes, Saturday 10-16!", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("m_reply_1", result.ProviderMessageId);
        Assert.Equal(200, result.HttpStatus);
        var call = http.Single;
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://graph.facebook.com/v21.0/17841400000001/messages", call.Url!.ToString());
        Assert.Equal("Bearer EAAB-page-token", call.Authorization);
        Assert.Equal("IGSID-7", call.Json.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal("RESPONSE", call.Json.GetProperty("messaging_type").GetString());
        Assert.Equal("Yes, Saturday 10-16!", call.Json.GetProperty("message").GetProperty("text").GetString());
        Assert.Equal(SourceType.Instagram, channel.SourceType);
    }

    [Fact]
    public async Task Meta_window_closed_error_is_mapped_to_a_400_fail()
    {
        var http = new StubHttp(HttpStatusCode.BadRequest, new
        {
            error = new { message = "(#10) This message is sent outside of allowed window.", type = "OAuthException", code = 10, error_subcode = 2018278, fbtrace_id = "A" },
        });
        var channel = new MetaReplyChannel(http.Client, SourceType.Facebook, NullLogger<MetaReplyChannel>.Instance);

        var result = await channel.SendAsync(IgSource, IgConversation, "late", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Reply window closed", result.Error);
        Assert.Equal(400, result.HttpStatus);
    }

    [Fact]
    public async Task Meta_other_errors_transport_failures_and_missing_token_never_throw()
    {
        var refused = new StubHttp(HttpStatusCode.BadRequest, new { error = new { message = "Invalid OAuth access token.", code = 190 } });
        var r1 = await new MetaReplyChannel(refused.Client, SourceType.Instagram, NullLogger<MetaReplyChannel>.Instance).SendAsync(IgSource, IgConversation, "x", CancellationToken.None);
        Assert.False(r1.Succeeded);
        Assert.Contains("190", r1.Error);
        Assert.Contains("Invalid OAuth", r1.Error);

        var down = StubHttp.Throwing(new HttpRequestException("connection refused"));
        var r2 = await new MetaReplyChannel(down.Client, SourceType.Instagram, NullLogger<MetaReplyChannel>.Instance).SendAsync(IgSource, IgConversation, "x", CancellationToken.None);
        Assert.False(r2.Succeeded);
        Assert.Contains("unreachable", r2.Error);

        var timeout = StubHttp.Throwing(new TaskCanceledException("timed out"));
        var r3 = await new MetaReplyChannel(timeout.Client, SourceType.Instagram, NullLogger<MetaReplyChannel>.Instance).SendAsync(IgSource, IgConversation, "x", CancellationToken.None);
        Assert.False(r3.Succeeded);

        var noToken = new Source { Id = 2, WorkspaceId = 1, Type = SourceType.Instagram, Name = "x", KeyId = "k2", ConfigJson = "{}" };
        var r4 = await new MetaReplyChannel(new StubHttp(HttpStatusCode.OK).Client, SourceType.Instagram, NullLogger<MetaReplyChannel>.Instance).SendAsync(noToken, IgConversation, "x", CancellationToken.None);
        Assert.False(r4.Succeeded);
        Assert.Contains("pageAccessToken", r4.Error);
    }

    [Fact]
    public async Task Telegram_reply_uses_send_message_with_the_chat_id()
    {
        var http = new StubHttp(HttpStatusCode.OK, new { ok = true, result = new { message_id = 1234, chat = new { id = 777 } } });
        var channel = new TelegramReplyChannel(http.Client, NullLogger<TelegramReplyChannel>.Instance);
        var source = new Source { Id = 3, WorkspaceId = 1, Type = SourceType.Telegram, Name = "studio-bot", KeyId = "k3", ConfigJson = """{"botToken":"123:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef"}""" };
        var conversation = new Conversation { Id = 6, WorkspaceId = 1, SourceId = 3, ExternalThreadId = "777" };

        var result = await channel.SendAsync(source, conversation, "Còn chỗ nhé!", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("1234", result.ProviderMessageId);
        var call = http.Single;
        Assert.Equal("https://api.telegram.org/bot123:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef/sendMessage", call.Url!.ToString());
        Assert.Equal("777", call.Json.GetProperty("chat_id").GetString());
        Assert.Equal("Còn chỗ nhé!", call.Json.GetProperty("text").GetString());

        var refused = new StubHttp(HttpStatusCode.Forbidden, new { ok = false, error_code = 403, description = "Forbidden: bot was blocked by the user" });
        var fail = await new TelegramReplyChannel(refused.Client, NullLogger<TelegramReplyChannel>.Instance).SendAsync(source, conversation, "x", CancellationToken.None);
        Assert.False(fail.Succeeded);
        Assert.Contains("blocked by the user", fail.Error);
        Assert.Equal(403, fail.HttpStatus);
    }

    private sealed class StubThreads(ReplyThread thread) : ReplyThreadLookup(null!)
    {
        public override Task<ReplyThread> ForAsync(Conversation conversation, CancellationToken ct) => Task.FromResult(thread);
    }

    private static IOptions<ChannelOptions> Resend(string? apiKey = "re_test_key", string? from = "Studio <inbox@studio.example>")
        => Options.Create(new ChannelOptions { Resend = new ChannelOptions.ResendOptions { ApiKey = apiKey, From = from } });

    [Fact]
    public async Task Email_reply_threads_on_the_last_inbound_message_and_uses_the_source_sender()
    {
        var http = new StubHttp(HttpStatusCode.OK, new { id = "49a3999c-0ce1-4ea6-ab68-afcd6dc2e794" });
        var threads = new StubThreads(new ReplyThread("Quote for a brand kit", "<abc@mail.example>", "Studio"));
        var channel = new EmailReplyChannel(http.Client, Resend(), threads, SourceType.Email, NullLogger<EmailReplyChannel>.Instance);
        var source = new Source { Id = 4, WorkspaceId = 1, Type = SourceType.Email, Name = "studio-inbox", KeyId = "k4", ExternalAccountId = "hello@studio.example", ConfigJson = """{"fromAddress":"hello@studio.example"}""" };
        var conversation = new Conversation { Id = 7, WorkspaceId = 1, SourceId = 4, ExternalThreadId = "anna@example.com", SenderHandle = "anna@example.com" };

        var result = await channel.SendAsync(source, conversation, "Sure, quote attached tomorrow.", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("49a3999c-0ce1-4ea6-ab68-afcd6dc2e794", result.ProviderMessageId);
        var call = http.Single;
        Assert.Equal("https://api.resend.com/emails", call.Url!.ToString());
        Assert.Equal("Bearer re_test_key", call.Authorization);
        Assert.Equal("hello@studio.example", call.Json.GetProperty("from").GetString());
        Assert.Equal("anna@example.com", call.Json.GetProperty("to")[0].GetString());
        Assert.Equal("Re: Quote for a brand kit", call.Json.GetProperty("subject").GetString());
        Assert.Equal("<abc@mail.example>", call.Json.GetProperty("headers").GetProperty("In-Reply-To").GetString());
        Assert.Equal("Sure, quote attached tomorrow.", call.Json.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Email_reply_for_a_web_form_conversation_goes_to_the_sender_handle_with_a_default_subject()
    {
        var http = new StubHttp(HttpStatusCode.OK, new { id = "r2" });
        var threads = new StubThreads(new ReplyThread(null, null, "Studio"));
        var channel = new EmailReplyChannel(http.Client, Resend(), threads, SourceType.Generic, NullLogger<EmailReplyChannel>.Instance);
        var form = new Source { Id = 5, WorkspaceId = 1, Type = SourceType.Generic, Name = "website-contact", KeyId = "k5" };

        var ok = await channel.SendAsync(form, new Conversation { Id = 8, WorkspaceId = 1, SourceId = 5, ExternalThreadId = "bao@example.com", SenderHandle = "bao@example.com" }, "Thanks Bao!", CancellationToken.None);
        Assert.True(ok.Succeeded);
        Assert.Equal(SourceType.Generic, channel.SourceType);
        var call = http.Single;
        Assert.Equal("Studio <inbox@studio.example>", call.Json.GetProperty("from").GetString());
        Assert.Equal("bao@example.com", call.Json.GetProperty("to")[0].GetString());
        Assert.Equal("Reply from Studio", call.Json.GetProperty("subject").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, call.Json.GetProperty("headers").ValueKind);

        var noEmail = await channel.SendAsync(form, new Conversation { Id = 9, WorkspaceId = 1, SourceId = 5, ExternalThreadId = "chi", SenderName = "Chi" }, "x", CancellationToken.None);
        Assert.False(noEmail.Succeeded);
        Assert.Contains("no email address", noEmail.Error);
        Assert.Single(http.Calls);
    }

    [Fact]
    public async Task Email_reply_fails_clearly_when_resend_is_not_configured_or_refuses()
    {
        var threads = new StubThreads(new ReplyThread(null, null, "Studio"));
        var source = new Source { Id = 4, WorkspaceId = 1, Type = SourceType.Email, Name = "studio-inbox", KeyId = "k4", ConfigJson = "{}" };
        var conversation = new Conversation { Id = 7, WorkspaceId = 1, SourceId = 4, ExternalThreadId = "anna@example.com" };

        var noKey = await new EmailReplyChannel(new StubHttp(HttpStatusCode.OK).Client, Resend(apiKey: null), threads, SourceType.Email, NullLogger<EmailReplyChannel>.Instance)
            .SendAsync(source, conversation, "x", CancellationToken.None);
        Assert.Contains("RelayHub:Resend:ApiKey", noKey.Error);

        var noFrom = await new EmailReplyChannel(new StubHttp(HttpStatusCode.OK).Client, Resend(from: null), threads, SourceType.Email, NullLogger<EmailReplyChannel>.Instance)
            .SendAsync(source, conversation, "x", CancellationToken.None);
        Assert.Contains("RelayHub:Resend:From", noFrom.Error);

        var refused = new StubHttp(HttpStatusCode.UnprocessableEntity, new { statusCode = 422, name = "validation_error", message = "The from address is not verified" });
        var fail = await new EmailReplyChannel(refused.Client, Resend(), threads, SourceType.Email, NullLogger<EmailReplyChannel>.Instance)
            .SendAsync(source, conversation, "x", CancellationToken.None);
        Assert.False(fail.Succeeded);
        Assert.Equal(422, fail.HttpStatus);
        Assert.Contains("not verified", fail.Error);
    }
}
