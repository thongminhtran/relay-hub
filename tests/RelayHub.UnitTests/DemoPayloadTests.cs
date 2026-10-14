using System.Text.Json;
using System.Text.RegularExpressions;
using RelayHub.Core.Demo;
using RelayHub.Core.Messaging;

namespace RelayHub.UnitTests;

public class DemoPayloadTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-14T15:30:00Z");
    private static readonly DemoCustomer Anna = DemoCustomers.Pool[0];
    private const string WebsiteKeyId = "0123456789ab";

    [Theory]
    [InlineData("instagram", "instagram", DemoCatalog.InstagramAccountId)]
    [InlineData("facebook", "page", DemoCatalog.FacebookPageId)]
    public void Meta_channels_build_a_webhook_batch_addressed_to_the_demo_account(string channel, string expectedObject, string accountId)
    {
        var sim = DemoPayloads.Build(channel, Anna, "How much for a bakery site?", WebsiteKeyId, Now);

        Assert.Equal(channel, sim.Channel);
        Assert.Equal(IncomingKinds.Meta, sim.Kind);
        Assert.Equal("meta", sim.SourceKey);
        Assert.Equal("", sim.KeyId);

        using var doc = JsonDocument.Parse(sim.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal(expectedObject, root.GetProperty("object").GetString());
        var entry = Assert.Single(root.GetProperty("entry").EnumerateArray());
        Assert.Equal(accountId, entry.GetProperty("id").GetString());
        Assert.Equal(Now.ToUnixTimeMilliseconds(), entry.GetProperty("time").GetInt64());
        var messaging = Assert.Single(entry.GetProperty("messaging").EnumerateArray());
        Assert.Equal(Anna.MetaId, messaging.GetProperty("sender").GetProperty("id").GetString());
        Assert.Equal(accountId, messaging.GetProperty("recipient").GetProperty("id").GetString());
        Assert.Equal(Now.ToUnixTimeMilliseconds(), messaging.GetProperty("timestamp").GetInt64());
        Assert.StartsWith("m_demo_", messaging.GetProperty("message").GetProperty("mid").GetString());
        Assert.Equal("How much for a bakery site?", messaging.GetProperty("message").GetProperty("text").GetString());

        // Same rule as the Meta hook: ExternalId = first 32 hex of SHA-256 of the raw body.
        Assert.Matches("^[0-9a-f]{32}$", sim.ExternalId);
        Assert.Equal(DemoPayloads.MetaExternalId(sim.PayloadJson), sim.ExternalId);
    }

    [Fact]
    public void Telegram_builds_a_private_chat_update()
    {
        var sim = DemoPayloads.Build("telegram", Anna, "Is the deposit included?", WebsiteKeyId, Now);

        Assert.Equal(IncomingKinds.Telegram, sim.Kind);
        Assert.Equal(DemoCatalog.TelegramSource, sim.SourceKey);
        Assert.Equal("", sim.KeyId);

        using var doc = JsonDocument.Parse(sim.PayloadJson);
        var root = doc.RootElement;
        var updateId = root.GetProperty("update_id").GetInt32();
        Assert.True(updateId > 0);
        Assert.Equal($"tg:{updateId}", sim.ExternalId);

        var message = root.GetProperty("message");
        Assert.True(message.GetProperty("message_id").GetInt32() > 0);
        var from = message.GetProperty("from");
        Assert.Equal(Anna.TelegramId, from.GetProperty("id").GetInt64());
        Assert.False(from.GetProperty("is_bot").GetBoolean());
        Assert.Equal("Anna", from.GetProperty("first_name").GetString());
        Assert.Equal("Nguyen", from.GetProperty("last_name").GetString());
        Assert.Equal("anna.bakes", from.GetProperty("username").GetString());
        var chat = message.GetProperty("chat");
        Assert.Equal(Anna.TelegramId, chat.GetProperty("id").GetInt64());
        Assert.Equal("private", chat.GetProperty("type").GetString());
        Assert.Equal(Now.ToUnixTimeSeconds(), message.GetProperty("date").GetInt64());
        Assert.Equal("Is the deposit included?", message.GetProperty("text").GetString());
    }

    [Fact]
    public void Email_builds_the_email_worker_post_and_uses_the_message_id_as_external_id()
    {
        var sim = DemoPayloads.Build("email", Anna, "Quote for a bakery website\n\nHi, could you send a quote?", WebsiteKeyId, Now);

        Assert.Equal(IncomingKinds.Email, sim.Kind);
        Assert.Equal("email", sim.SourceKey);
        Assert.Equal("", sim.KeyId);

        using var doc = JsonDocument.Parse(sim.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal(DemoCatalog.EmailAddress, root.GetProperty("to").GetString());
        Assert.Equal(Anna.Email, root.GetProperty("from").GetString());
        Assert.Equal(Anna.Name, root.GetProperty("fromName").GetString());
        Assert.Equal("Quote for a bakery website", root.GetProperty("subject").GetString());
        Assert.Equal("Hi, could you send a quote?", root.GetProperty("text").GetString());
        Assert.Equal(Now, DateTimeOffset.Parse(root.GetProperty("receivedAt").GetString()!));

        var messageId = root.GetProperty("messageId").GetString()!;
        Assert.Matches(new Regex("^<demo-[0-9a-f]{32}@relayhub\\.dev>$"), messageId);
        Assert.Equal(messageId, sim.ExternalId);
    }

    [Fact]
    public void Single_paragraph_email_takes_its_first_sentence_as_subject()
    {
        var (subject, body) = DemoPayloads.SplitEmail("Is the studio open on Saturdays? I'd like to come by.");
        Assert.Equal("Is the studio open on Saturdays?", subject);
        Assert.Equal("Is the studio open on Saturdays? I'd like to come by.", body);

        var longText = new string('a', 120);
        var (longSubject, _) = DemoPayloads.SplitEmail(longText);
        Assert.Equal(80, longSubject.Length);
        Assert.EndsWith("…", longSubject);
    }

    [Fact]
    public void Web_builds_the_contact_form_body_with_the_source_key_id()
    {
        var sim = DemoPayloads.Build("web", Anna, "Can you send the final logo files as SVG?", WebsiteKeyId, Now);

        Assert.Equal(IncomingKinds.Generic, sim.Kind);
        Assert.Equal(DemoCatalog.WebsiteSource, sim.SourceKey);
        Assert.Equal(WebsiteKeyId, sim.KeyId);
        Assert.Matches("^[0-9a-f]{32}$", sim.ExternalId); // fresh guid per message, like the generic hook without Idempotency-Key

        using var doc = JsonDocument.Parse(sim.PayloadJson);
        Assert.Equal(Anna.Name, doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(Anna.Email, doc.RootElement.GetProperty("email").GetString());
        Assert.Equal("Can you send the final logo files as SVG?", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Every_simulated_message_gets_a_new_external_id()
    {
        foreach (var channel in DemoChannels.All)
        {
            var a = DemoPayloads.Build(channel, Anna, "same text", WebsiteKeyId, Now);
            var b = DemoPayloads.Build(channel, Anna, "same text", WebsiteKeyId, Now);
            Assert.NotEqual(a.ExternalId, b.ExternalId);
        }
    }

    [Fact]
    public void Unknown_channel_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DemoPayloads.Build("whatsapp", Anna, "hi", WebsiteKeyId, Now));
        Assert.False(DemoChannels.IsKnown("whatsapp"));
        Assert.True(DemoChannels.IsKnown(" Instagram "));
    }

    [Fact]
    public void Default_texts_exist_for_every_channel()
    {
        foreach (var channel in DemoChannels.All)
            Assert.False(string.IsNullOrWhiteSpace(DemoPayloads.DefaultText(channel)));
    }

    [Fact]
    public void Customers_resolve_to_stable_ids_so_conversations_accumulate()
    {
        Assert.Same(Anna, DemoCustomers.Resolve("anna nguyen"));
        Assert.Same(Anna, DemoCustomers.Resolve("@anna.bakes"));
        Assert.Contains(DemoCustomers.Resolve(null), DemoCustomers.Pool);
        Assert.Contains(DemoCustomers.Resolve("  "), DemoCustomers.Pool);

        var first = DemoCustomers.Resolve("Minh  Le");
        var again = DemoCustomers.Resolve("minh le");
        Assert.Equal("Minh Le", first.Name);
        Assert.Equal("Minh", first.FirstName);
        Assert.Equal("Le", first.LastName);
        Assert.Equal("minh.le", first.Handle);
        Assert.Equal("minh.le@example.com", first.Email);
        Assert.Equal(first.MetaId, again.MetaId);
        Assert.Equal(first.TelegramId, again.TelegramId);
        Assert.Matches("^[0-9]{17}$", first.MetaId);
        Assert.DoesNotContain(DemoCustomers.Pool, c => c.MetaId == first.MetaId || c.TelegramId == first.TelegramId);
        Assert.NotEqual(first.MetaId, DemoCustomers.FromName("Priya Patel").MetaId);
    }

    [Fact]
    public void Pool_ids_are_distinct()
    {
        Assert.Equal(DemoCustomers.Pool.Count, DemoCustomers.Pool.Select(c => c.MetaId).Distinct().Count());
        Assert.Equal(DemoCustomers.Pool.Count, DemoCustomers.Pool.Select(c => c.TelegramId).Distinct().Count());
        Assert.Equal(DemoCustomers.Pool.Count, DemoCustomers.Pool.Select(c => c.Email).Distinct().Count());
    }
}
