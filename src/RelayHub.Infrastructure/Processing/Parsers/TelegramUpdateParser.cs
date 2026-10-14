using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Infrastructure.Processing.Parsers;

/// <summary>
/// One Telegram Bot API <c>Update</c>: <c>{ update_id, message: { message_id, from { id, first_name, last_name, username }, chat { id, type },
/// date, text | caption, photo | document | voice | ... } }</c>. The source is the enabled Telegram source whose Name is the
/// <c>{sourceKey}</c> of the webhook URL; the conversation is the chat id. Edits, channel posts and callback queries produce nothing.
/// </summary>
public sealed class TelegramUpdateParser(RelayHubDbContext db, ILogger<TelegramUpdateParser> log) : ISourceParser
{
    private static readonly string[] AttachmentKinds =
        ["photo", "document", "voice", "video", "audio", "sticker", "video_note", "animation", "location", "contact", "poll"];

    public string Kind => IncomingKinds.Telegram;

    public async Task<ParseResult> ParseAsync(IncomingMessage msg, CancellationToken ct)
    {
        var sources = await db.Sources.IgnoreQueryFilters()
            .Where(s => s.Type == SourceType.Telegram && s.IsEnabled && s.Name == msg.SourceKey)
            .ToListAsync(ct);
        if (sources.Count != 1)
        {
            var reason = sources.Count == 0
                ? $"no enabled Telegram source named {msg.SourceKey}"
                : $"{sources.Count} enabled Telegram sources are named {msg.SourceKey}; rename one so the webhook URL is unambiguous";
            log.LogWarning("Telegram update dropped: {Reason}", reason);
            return ParseResult.Dropped(reason);
        }
        return Parse(msg, sources[0]);
    }

    public static ParseResult Parse(IncomingMessage msg, Source source)
    {
        if (ParserJson.ParseObject(msg.PayloadJson) is not { } update)
            return ParseResult.Dropped($"Telegram payload is not a JSON object (source {source.Name})");

        if (update.GetObject("message") is not { } message) return ParseResult.Nothing; // edited_message, channel_post, callback_query, ...
        var chatId = message.GetObject("chat")?.GetIdString("id");
        if (string.IsNullOrEmpty(chatId)) return ParseResult.Dropped($"Telegram message has no chat id (source {source.Name})");

        var from = message.GetObject("from");
        var firstName = from?.GetString("first_name");
        var lastName = from?.GetString("last_name");
        var username = from?.GetString("username");
        var senderName = string.Join(" ", new[] { firstName, lastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (senderName.Length == 0) senderName = message.GetObject("chat")?.GetString("title") ?? "Telegram user";

        var externalId = msg.ExternalId;
        if (string.IsNullOrWhiteSpace(externalId) && message.GetInt64("message_id") is { } messageId) externalId = $"tg:{chatId}:{messageId}";
        if (string.IsNullOrWhiteSpace(externalId)) return ParseResult.Dropped($"Telegram update has no id (source {source.Name})");

        return ParseResult.One(new ParsedInbound(
            source,
            ExternalId: ParserJson.Clip(externalId, 200),
            ExternalThreadId: ParserJson.Clip(chatId, 200),
            SenderName: ParserJson.Clip(senderName, 200),
            SenderHandle: string.IsNullOrWhiteSpace(username) ? null : ParserJson.Clip("@" + username, 200),
            Text: Describe(message),
            PayloadJson: update.GetRawText(),
            At: ParserJson.FromUnix(message.GetInt64("date"), milliseconds: false, msg.ReceivedAt),
            Channel: "telegram"));
    }

    private static string Describe(JsonElement message)
    {
        var text = (message.GetString("text") ?? message.GetString("caption"))?.Trim() ?? "";
        var markers = AttachmentKinds.Where(k => message.TryGetProperty(k, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                                     .Select(k => $"[attachment: {k}]").ToList();
        if (markers.Count > 0) text = text.Length == 0 ? string.Join("\n", markers) : text + "\n" + string.Join("\n", markers);
        return text.Length > 0 ? text : "[unsupported message]";
    }
}
