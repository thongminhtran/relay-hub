using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Infrastructure.Processing.Parsers;

/// <summary>
/// One Meta webhook batch (Messenger Platform): <c>{ object: "page" | "instagram", entry: [ { id, time, messaging: [ { sender.id,
/// recipient.id, timestamp, message { mid, text, attachments[], is_echo, is_deleted, is_unsupported } | postback | read | delivery | reaction } ] } ] }</c>.
/// Each <c>entry.id</c> (Page id or Instagram professional account id) finds the Source by <see cref="Source.ExternalAccountId"/>;
/// echoes (our own replies), read receipts, deliveries and reactions produce nothing; every stored message dedupes on <c>mid</c>.
/// </summary>
public sealed class MetaWebhookParser(RelayHubDbContext db, ILogger<MetaWebhookParser> log) : ISourceParser
{
    public string Kind => IncomingKinds.Meta;

    public async Task<ParseResult> ParseAsync(IncomingMessage msg, CancellationToken ct)
    {
        if (ParserJson.ParseObject(msg.PayloadJson) is not { } root)
            return ParseResult.Dropped("Meta payload is not a JSON object");

        var type = SourceTypeFor(root.GetString("object"));
        if (type is null) return ParseResult.Nothing; // a webhook topic we did not subscribe to (user, permissions, ...)

        var accountIds = root.Items("entry").Select(e => e.GetIdString("id")).OfType<string>().Distinct().ToList();
        if (accountIds.Count == 0) return ParseResult.Dropped("Meta payload has no entry ids");

        var sources = await db.Sources.IgnoreQueryFilters()
            .Where(s => s.Type == type && s.IsEnabled && s.ExternalAccountId != null && accountIds.Contains(s.ExternalAccountId))
            .ToListAsync(ct);

        var result = Parse(root, msg.ReceivedAt, sources);
        if (result.DropReason is not null) log.LogWarning("Meta webhook dropped: {Reason}", result.DropReason);
        return result;
    }

    /// <summary>Pure part, unit-testable: <paramref name="sources"/> are the enabled Facebook / Instagram sources whose ExternalAccountId may appear in the batch.</summary>
    public static ParseResult Parse(JsonElement root, DateTimeOffset receivedAt, IReadOnlyCollection<Source> sources)
    {
        var type = SourceTypeFor(root.GetString("object"));
        if (type is null) return ParseResult.Nothing;
        var channel = type == SourceType.Instagram ? "instagram" : "facebook";

        var items = new List<ParsedInbound>();
        var unknownAccounts = new List<string>();

        foreach (var entry in root.Items("entry"))
        {
            var accountId = entry.GetIdString("id");
            if (accountId is null) continue;
            var source = sources.FirstOrDefault(s => s.Type == type && s.IsEnabled && string.Equals(s.ExternalAccountId, accountId, StringComparison.Ordinal));
            if (source is null)
            {
                unknownAccounts.Add(accountId);
                continue;
            }

            foreach (var evt in entry.Items("messaging"))
            {
                if (ToInbound(evt, source, channel, receivedAt) is { } item) items.Add(item);
            }
        }

        if (items.Count == 0 && unknownAccounts.Count > 0)
            return ParseResult.Dropped($"no enabled {type} source has ExternalAccountId {string.Join(", ", unknownAccounts.Distinct())}");
        return new ParseResult(items);
    }

    private static ParsedInbound? ToInbound(JsonElement evt, Source source, string channel, DateTimeOffset receivedAt)
    {
        var senderId = evt.GetObject("sender")?.GetIdString("id");
        if (string.IsNullOrEmpty(senderId)) return null;
        var at = ParserJson.FromUnix(evt.GetInt64("timestamp"), milliseconds: true, receivedAt);

        string? mid, text;
        if (evt.GetObject("message") is { } message)
        {
            if (message.GetBool("is_echo") || message.GetBool("is_deleted")) return null; // our own reply echoed back / customer unsent it
            mid = message.GetString("mid");
            text = DescribeMessage(message);
        }
        else if (evt.GetObject("postback") is { } postback)
        {
            mid = postback.GetString("mid") ?? $"postback:{senderId}:{at.ToUnixTimeMilliseconds()}";
            text = $"[postback: {postback.GetString("title") ?? postback.GetString("payload") ?? "button"}]";
        }
        else
        {
            return null; // read, delivery, reaction, referral, ... — nothing to show in the inbox
        }
        if (string.IsNullOrEmpty(mid)) return null;

        return new ParsedInbound(
            source,
            ExternalId: ParserJson.Clip(mid, 200),
            ExternalThreadId: ParserJson.Clip(senderId, 200),
            SenderName: channel == "instagram" ? "Instagram user" : "Facebook user", // Meta sends only the scoped id; a profile lookup is a later step
            SenderHandle: ParserJson.Clip(senderId, 200),
            Text: text,
            PayloadJson: evt.GetRawText(),
            At: at,
            Channel: channel);
    }

    /// <summary>Text, plus one <c>[attachment: image]</c> line per attachment; <c>[unsupported message]</c> when Meta sent neither.</summary>
    private static string DescribeMessage(JsonElement message)
    {
        var text = message.GetString("text")?.Trim() ?? "";
        var markers = message.Items("attachments").Select(a => $"[attachment: {a.GetString("type") ?? "file"}]").ToList();
        if (markers.Count > 0) text = text.Length == 0 ? string.Join("\n", markers) : text + "\n" + string.Join("\n", markers);
        return text.Length > 0 ? text : "[unsupported message]";
    }

    private static SourceType? SourceTypeFor(string? obj) => obj switch
    {
        "page" => SourceType.Facebook,
        "instagram" => SourceType.Instagram,
        _ => null,
    };
}
