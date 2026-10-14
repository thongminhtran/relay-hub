using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayHub.Core.Messaging;

namespace RelayHub.Core.Demo;

/// <summary>
/// What the simulate endpoint hands to the queue: the same fields the real hook would set after verifying the provider's
/// signature (<see cref="IncomingMessage"/> minus the request-specific correlation / trace ids).
/// </summary>
public sealed record SimulatedIncoming(string Channel, string Kind, string SourceKey, string KeyId, string ExternalId, string PayloadJson);

/// <summary>
/// Builds provider-shaped webhook payloads for the demo workspace so a simulated message flows through the same parsers as a
/// real one (Meta Messenger / Instagram webhook batch, Telegram Update, Cloudflare Email Worker post, website form).
/// The ExternalId rules mirror the hooks: Meta = first 32 hex of SHA-256 of the body, Telegram = "tg:" + update_id,
/// email = messageId, web = a fresh guid.
/// </summary>
public static class DemoPayloads
{
    public const string MetaSourceKey = "meta";
    public const string EmailSourceKey = "email";
    public const string MetaMidPrefix = "m_demo_";
    public const string EmailMessageIdPrefix = "<demo-";
    public const string EmailMessageIdSuffix = "@relayhub.dev>";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly IReadOnlyDictionary<string, string[]> DefaultTexts = new Dictionary<string, string[]>
    {
        [DemoChannels.Instagram] =
        [
            "Hi! Love your work. How much for a simple website for my bakery?",
            "Could you send the logo in a warmer colour? The orange feels a bit too bright.",
            "Do you also design Instagram post templates?",
            "Saw your reel about the restaurant site — can you do something similar for a cafe?",
        ],
        [DemoChannels.Facebook] =
        [
            "Hello, we run a small restaurant. Do you build websites with an online menu?",
            "Is the studio open this Saturday? I'd like to drop by with my laptop.",
            "Can you redo our Facebook cover photo to match the new logo?",
            "What's a rough price for a 5-page website with a booking form?",
        ],
        [DemoChannels.Telegram] =
        [
            "Hi, quick question about invoice #1042 — is the deposit included?",
            "Our booking page is broken on mobile, can you take a look?",
            "When can we schedule the kickoff call for the new site?",
            "Got the mock-ups, love the second option. Can we go with that?",
        ],
        [DemoChannels.Email] =
        [
            "We're opening a second location and need a site with the menu and reservations. Could you send a quote?",
            "Please find attached our feedback on the logo revisions. Two small changes and we're done!",
            "Can you resend invoice #1042 as a PDF? Our accountant can't open the link.",
            "Is the studio open on Saturdays? I'd like to come by and discuss a rebrand.",
        ],
        [DemoChannels.Web] =
        [
            "I'd like a quote for a small business website (5 pages, contact form).",
            "Can you send the final logo files as SVG?",
            "Is the studio open on Saturdays?",
            "We need a landing page for our gym before January. What's your timeline?",
        ],
    };

    /// <summary>A believable default message for the channel when the visitor typed none.</summary>
    public static string DefaultText(string channel)
    {
        var texts = DefaultTexts[Canonical(channel)];
        return texts[Random.Shared.Next(texts.Length)];
    }

    /// <summary>
    /// Builds the payload and queue fields for one simulated message.
    /// </summary>
    /// <param name="websiteKeyId">KeyId of the demo website source (generic hooks carry the key id; provider hooks carry none).</param>
    public static SimulatedIncoming Build(string channel, DemoCustomer sender, string text, string websiteKeyId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return Canonical(channel) switch
        {
            DemoChannels.Instagram => Meta(DemoChannels.Instagram, "instagram", DemoCatalog.InstagramAccountId, sender, text, now),
            DemoChannels.Facebook => Meta(DemoChannels.Facebook, "page", DemoCatalog.FacebookPageId, sender, text, now),
            DemoChannels.Telegram => Telegram(sender, text, now),
            DemoChannels.Email => Email(sender, text, now),
            DemoChannels.Web => Web(sender, text, websiteKeyId),
            var other => throw new ArgumentOutOfRangeException(nameof(channel), other, "Unknown demo channel"),
        };
    }

    /// <summary>
    /// Subject + body of a simulated email. Text with several paragraphs: the first paragraph is the subject. A single
    /// paragraph: the subject is its first sentence, capped at 80 characters, and the body is the whole text.
    /// The worker stores an email as subject + "\n\n" + body, so <c>subject + "\n\n" + body</c> round-trips the input.
    /// </summary>
    public static (string Subject, string Body) SplitEmail(string text)
    {
        text = text.Trim();
        var paragraphBreak = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (paragraphBreak > 0)
            return (text[..paragraphBreak].Trim().ReplaceLineEndings(" "), text[(paragraphBreak + 2)..].Trim());

        var firstLine = text.Split('\n', 2)[0].Trim();
        var end = firstLine.IndexOfAny(['.', '?', '!']);
        var subject = end > 0 ? firstLine[..(end + 1)] : firstLine;
        return (subject.Length <= 80 ? subject : subject[..79].TrimEnd() + "…", text);
    }

    private static SimulatedIncoming Meta(string channel, string objectName, string accountId, DemoCustomer sender, string text, DateTimeOffset now)
    {
        var ms = now.ToUnixTimeMilliseconds();
        var payload = JsonSerializer.Serialize(new
        {
            @object = objectName,
            entry = new[]
            {
                new
                {
                    id = accountId,
                    time = ms,
                    messaging = new[]
                    {
                        new
                        {
                            sender = new { id = sender.MetaId },
                            recipient = new { id = accountId },
                            timestamp = ms,
                            message = new { mid = MetaMidPrefix + Guid.NewGuid().ToString("N"), text },
                        },
                    },
                },
            },
        }, Json);
        return new SimulatedIncoming(channel, IncomingKinds.Meta, MetaSourceKey, KeyId: "", MetaExternalId(payload), payload);
    }

    /// <summary>Same rule as the Meta hook: the body hash identifies a webhook batch.</summary>
    public static string MetaExternalId(string payloadJson)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)))[..32];

    private static SimulatedIncoming Telegram(DemoCustomer sender, string text, DateTimeOffset now)
    {
        var updateId = Random.Shared.Next(100_000_000, int.MaxValue);
        var payload = JsonSerializer.Serialize(new
        {
            update_id = updateId,
            message = new
            {
                message_id = Random.Shared.Next(1, 1_000_000),
                from = new { id = sender.TelegramId, is_bot = false, first_name = sender.FirstName, last_name = NullIfEmpty(sender.LastName), username = sender.Handle },
                chat = new { id = sender.TelegramId, type = "private" },
                date = now.ToUnixTimeSeconds(),
                text,
            },
        }, Json);
        return new SimulatedIncoming(DemoChannels.Telegram, IncomingKinds.Telegram, DemoCatalog.TelegramSource, KeyId: "", $"tg:{updateId}", payload);
    }

    private static SimulatedIncoming Email(DemoCustomer sender, string text, DateTimeOffset now)
    {
        var messageId = $"{EmailMessageIdPrefix}{Guid.NewGuid():N}{EmailMessageIdSuffix}";
        var (subject, body) = SplitEmail(text);
        var payload = JsonSerializer.Serialize(new
        {
            to = DemoCatalog.EmailAddress,
            from = sender.Email,
            fromName = sender.Name,
            subject,
            text = body,
            messageId,
            receivedAt = now.ToString("O"),
        }, Json);
        return new SimulatedIncoming(DemoChannels.Email, IncomingKinds.Email, EmailSourceKey, KeyId: "", messageId, payload);
    }

    private static SimulatedIncoming Web(DemoCustomer sender, string text, string websiteKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(websiteKeyId);
        var payload = JsonSerializer.Serialize(new { name = sender.Name, email = sender.Email, message = text }, Json);
        return new SimulatedIncoming(DemoChannels.Web, IncomingKinds.Generic, DemoCatalog.WebsiteSource, websiteKeyId, Guid.NewGuid().ToString("N"), payload);
    }

    private static string Canonical(string channel) => (channel ?? "").Trim().ToLowerInvariant();

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
