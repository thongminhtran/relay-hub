using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Processing.Parsers;

namespace RelayHub.UnitTests.Channels;

public class TelegramUpdateParserTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Source Bot = new() { Id = 20, WorkspaceId = 1, Type = SourceType.Telegram, Name = "studio-bot", KeyId = "k", ConfigJson = """{"botToken":"1:x"}""" };

    private static IncomingMessage Msg(string json, string externalId = "tg:900001")
        => new("", "studio-bot", externalId, json, ReceivedAt, "corr", null, IncomingKinds.Telegram);

    [Fact]
    public void Text_message_maps_chat_sender_and_date()
    {
        var result = TelegramUpdateParser.Parse(Msg("""
            {"update_id":900001,"message":{"message_id":55,"from":{"id":777,"is_bot":false,"first_name":"Linh","last_name":"Nguyen","username":"linhng"},
             "chat":{"id":777,"first_name":"Linh","type":"private"},"date":1760443200,"text":"Xin chào, còn chỗ tuần sau không?"}}
            """), Bot);

        var item = Assert.Single(result.Items);
        Assert.Equal("telegram", item.Channel);
        Assert.Equal("tg:900001", item.ExternalId);
        Assert.Equal("777", item.ExternalThreadId);
        Assert.Equal("Linh Nguyen", item.SenderName);
        Assert.Equal("@linhng", item.SenderHandle);
        Assert.Equal("Xin chào, còn chỗ tuần sau không?", item.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760443200), item.At);
    }

    [Fact]
    public void Photo_with_caption_and_user_without_username()
    {
        var result = TelegramUpdateParser.Parse(Msg("""
            {"update_id":900002,"message":{"message_id":56,"from":{"id":778,"is_bot":false,"first_name":"Minh"},
             "chat":{"id":-100123,"title":"Studio group","type":"supergroup"},"date":1760443300,"caption":"reference pic",
             "photo":[{"file_id":"a","width":90,"height":90},{"file_id":"b","width":800,"height":800}]}}
            """), Bot);

        var item = Assert.Single(result.Items);
        Assert.Equal("-100123", item.ExternalThreadId);
        Assert.Equal("Minh", item.SenderName);
        Assert.Null(item.SenderHandle);
        Assert.Equal("reference pic\n[attachment: photo]", item.Text);
    }

    [Fact]
    public void Edited_messages_and_callback_queries_produce_nothing_and_garbage_is_dropped()
    {
        Assert.Empty(TelegramUpdateParser.Parse(Msg("""{"update_id":1,"edited_message":{"message_id":1,"chat":{"id":1},"date":1,"text":"x"}}"""), Bot).Items);
        Assert.Null(TelegramUpdateParser.Parse(Msg("""{"update_id":2,"callback_query":{"id":"q"}}"""), Bot).DropReason);

        Assert.NotNull(TelegramUpdateParser.Parse(Msg("not json"), Bot).DropReason);
        Assert.NotNull(TelegramUpdateParser.Parse(Msg("""{"update_id":3,"message":{"message_id":1,"date":1,"text":"no chat"}}"""), Bot).DropReason);
    }
}

public class EmailParserTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Source Inbox = new() { Id = 30, WorkspaceId = 1, Type = SourceType.Email, Name = "studio-inbox", KeyId = "k", ExternalAccountId = "hello@studio.example" };

    private static IncomingMessage Msg(string json, string externalId = "<abc@mail.example>")
        => new("", "email", externalId, json, ReceivedAt, "corr", null, IncomingKinds.Email);

    [Fact]
    public void Subject_and_text_are_joined_and_the_sender_address_is_the_thread()
    {
        var result = EmailParser.Parse(Msg("""
            {"to":"hello@studio.example","from":"Anna Lee <Anna@Example.com>","subject":"Quote for a brand kit","text":"Hi,\n\nCould you send a quote?","html":"<p>ignored when text exists</p>",
             "messageId":"<abc@mail.example>","receivedAt":"2026-10-14T11:58:00Z"}
            """), Inbox);

        var item = Assert.Single(result.Items);
        Assert.Equal("email", item.Channel);
        Assert.Equal("<abc@mail.example>", item.ExternalId);
        Assert.Equal("anna@example.com", item.ExternalThreadId);
        Assert.Equal("anna@example.com", item.SenderHandle);
        Assert.Equal("Anna Lee", item.SenderName);
        Assert.Equal("Quote for a brand kit\n\nHi,\n\nCould you send a quote?", item.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 11, 58, 0, TimeSpan.Zero), item.At);
    }

    [Fact]
    public void Html_is_stripped_when_there_is_no_text_and_the_name_falls_back_to_the_local_part()
    {
        var result = EmailParser.Parse(Msg("""
            {"to":"hello@studio.example","from":"bao.tran@example.com","subject":"","html":"<div>Hello<br>there &amp; thanks</div>","messageId":"<x@y>"}
            """), Inbox);

        var item = Assert.Single(result.Items);
        Assert.Equal("bao.tran", item.SenderName);
        Assert.Equal("Hello\nthere & thanks", item.Text);
        Assert.Equal(ReceivedAt, item.At);
    }

    [Fact]
    public void Missing_from_is_dropped()
    {
        Assert.NotNull(EmailParser.Parse(Msg("""{"to":"hello@studio.example","subject":"x","text":"y","messageId":"<x@y>"}"""), Inbox).DropReason);
        Assert.NotNull(EmailParser.Parse(Msg("""{"to":"hello@studio.example","from":"not an address","text":"y","messageId":"<x@y>"}"""), Inbox).DropReason);
    }

    [Theory]
    [InlineData("hello@studio.example", "hello@studio.example")]
    [InlineData("Studio <Hello@Studio.Example>", "hello@studio.example")]
    [InlineData("  hello@studio.example  ", "hello@studio.example")]
    [InlineData("nope", null)]
    [InlineData("", null)]
    public void Address_extraction(string input, string? expected)
    {
        Assert.Equal(expected, EmailParser.AddressOf(input));
    }
}
