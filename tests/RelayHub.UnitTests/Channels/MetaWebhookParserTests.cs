using System.Text.Json;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Processing.Parsers;

namespace RelayHub.UnitTests.Channels;

public class MetaWebhookParserTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly Source Page = new()
    {
        Id = 10, WorkspaceId = 1, Type = SourceType.Facebook, Name = "studio-page", KeyId = "k1", ExternalAccountId = "100200300",
        ConfigJson = """{"pageAccessToken":"EAAB..."}""",
    };

    private static readonly Source Instagram = new()
    {
        Id = 11, WorkspaceId = 1, Type = SourceType.Instagram, Name = "studio-ig", KeyId = "k2", ExternalAccountId = "17841400000001",
    };

    private static ParseResult Parse(string json, params Source[] sources)
    {
        using var doc = JsonDocument.Parse(json);
        return MetaWebhookParser.Parse(doc.RootElement.Clone(), ReceivedAt, sources);
    }

    [Fact]
    public void Page_text_message_becomes_a_facebook_event_keyed_by_mid_and_psid()
    {
        var result = Parse("""
            {"object":"page","entry":[{"id":"100200300","time":1760443200000,"messaging":[
              {"sender":{"id":"PSID-42"},"recipient":{"id":"100200300"},"timestamp":1760443200123,
               "message":{"mid":"m_abc123","text":"Hi, do you do logo design?"}}]}]}
            """, Page, Instagram);

        var item = Assert.Single(result.Items);
        Assert.Null(result.DropReason);
        Assert.Same(Page, item.Source);
        Assert.Equal("facebook", item.Channel);
        Assert.Equal("m_abc123", item.ExternalId);
        Assert.Equal("PSID-42", item.ExternalThreadId);
        Assert.Equal("PSID-42", item.SenderHandle);
        Assert.Equal("Facebook user", item.SenderName);
        Assert.Equal("Hi, do you do logo design?", item.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760443200123), item.At);
        Assert.Contains("\"mid\":\"m_abc123\"", item.PayloadJson); // the payload stored is the single messaging event, not the batch
    }

    [Fact]
    public void Instagram_message_is_matched_on_the_ig_account_id_with_channel_instagram()
    {
        var result = Parse("""
            {"object":"instagram","entry":[{"id":"17841400000001","time":1760443200,"messaging":[
              {"sender":{"id":"IGSID-7"},"recipient":{"id":"17841400000001"},"timestamp":1760443200000,
               "message":{"mid":"aWdfZAG1faXRlbTox","text":"Is the studio open Saturday?"}}]}]}
            """, Page, Instagram);

        var item = Assert.Single(result.Items);
        Assert.Same(Instagram, item.Source);
        Assert.Equal("instagram", item.Channel);
        Assert.Equal("IGSID-7", item.ExternalThreadId);
        Assert.Equal("Instagram user", item.SenderName);
        Assert.Equal("Is the studio open Saturday?", item.Text);
    }

    [Fact]
    public void Echoes_read_receipts_and_deliveries_produce_nothing()
    {
        var result = Parse("""
            {"object":"page","entry":[{"id":"100200300","messaging":[
              {"sender":{"id":"100200300"},"recipient":{"id":"PSID-42"},"timestamp":1,"message":{"mid":"m_echo","is_echo":true,"text":"our own reply"}},
              {"sender":{"id":"PSID-42"},"recipient":{"id":"100200300"},"timestamp":2,"read":{"watermark":1760443200000}},
              {"sender":{"id":"PSID-42"},"recipient":{"id":"100200300"},"timestamp":3,"delivery":{"mids":["m_x"],"watermark":1760443200000}},
              {"sender":{"id":"PSID-42"},"recipient":{"id":"100200300"},"timestamp":4,"reaction":{"mid":"m_x","action":"react","emoji":"❤"}},
              {"sender":{"id":"PSID-42"},"recipient":{"id":"100200300"},"timestamp":5,"message":{"mid":"m_del","is_deleted":true}}]}]}
            """, Page);

        Assert.Empty(result.Items);
        Assert.Null(result.DropReason); // valid batch, nothing to store → Ignored, not Dropped
    }

    [Fact]
    public void Attachment_only_message_is_described_and_text_plus_attachment_keeps_both()
    {
        var result = Parse("""
            {"object":"instagram","entry":[{"id":"17841400000001","messaging":[
              {"sender":{"id":"IGSID-7"},"recipient":{"id":"17841400000001"},"timestamp":1760443200000,
               "message":{"mid":"m_img","attachments":[{"type":"image","payload":{"url":"https://lookaside.fbsbx.com/x.jpg"}}]}},
              {"sender":{"id":"IGSID-7"},"recipient":{"id":"17841400000001"},"timestamp":1760443201000,
               "message":{"mid":"m_both","text":"like this one","attachments":[{"type":"share","payload":{"url":"https://instagram.com/p/1"}},{"type":"video","payload":{"url":"https://x/v.mp4"}}]}},
              {"sender":{"id":"IGSID-7"},"recipient":{"id":"17841400000001"},"timestamp":1760443202000,
               "message":{"mid":"m_unsupported","is_unsupported":true}}]}]}
            """, Instagram);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal("[attachment: image]", result.Items[0].Text);
        Assert.Equal("like this one\n[attachment: share]\n[attachment: video]", result.Items[1].Text);
        Assert.Equal("[unsupported message]", result.Items[2].Text);
    }

    [Fact]
    public void Unknown_page_id_is_dropped_with_the_id_in_the_reason()
    {
        var result = Parse("""
            {"object":"page","entry":[{"id":"999","messaging":[
              {"sender":{"id":"PSID-1"},"recipient":{"id":"999"},"timestamp":1,"message":{"mid":"m_1","text":"hello"}}]}]}
            """, Page, Instagram);

        Assert.Empty(result.Items);
        Assert.Contains("999", result.DropReason);
    }

    [Fact]
    public void Batch_with_several_entries_yields_one_item_per_message_and_skips_unknown_pages()
    {
        var result = Parse("""
            {"object":"page","entry":[
              {"id":"100200300","messaging":[
                {"sender":{"id":"A"},"recipient":{"id":"100200300"},"timestamp":1,"message":{"mid":"m_1","text":"one"}},
                {"sender":{"id":"B"},"recipient":{"id":"100200300"},"timestamp":2,"message":{"mid":"m_2","text":"two"}}]},
              {"id":"999","messaging":[
                {"sender":{"id":"C"},"recipient":{"id":"999"},"timestamp":3,"message":{"mid":"m_3","text":"three"}}]}]}
            """, Page);

        Assert.Equal(["m_1", "m_2"], result.Items.Select(i => i.ExternalId));
        Assert.Null(result.DropReason);
    }

    [Fact]
    public void Postback_is_stored_as_a_button_press_and_other_webhook_objects_are_ignored()
    {
        var postback = Parse("""
            {"object":"page","entry":[{"id":"100200300","messaging":[
              {"sender":{"id":"PSID-42"},"recipient":{"id":"100200300"},"timestamp":1,"postback":{"mid":"m_pb","title":"Get Started","payload":"GET_STARTED"}}]}]}
            """, Page);
        Assert.Equal("[postback: Get Started]", Assert.Single(postback.Items).Text);

        var other = Parse("""{"object":"permissions","entry":[{"id":"100200300","changes":[]}]}""", Page);
        Assert.Empty(other.Items);
        Assert.Null(other.DropReason);
    }

    [Fact]
    public void Disabled_source_is_treated_as_unknown()
    {
        var disabled = new Source { Id = 12, WorkspaceId = 1, Type = SourceType.Facebook, Name = "old-page", KeyId = "k3", ExternalAccountId = "100200300", IsEnabled = false };
        var result = Parse("""
            {"object":"page","entry":[{"id":"100200300","messaging":[
              {"sender":{"id":"A"},"recipient":{"id":"100200300"},"timestamp":1,"message":{"mid":"m_1","text":"one"}}]}]}
            """, disabled);

        Assert.Empty(result.Items);
        Assert.NotNull(result.DropReason);
    }
}
