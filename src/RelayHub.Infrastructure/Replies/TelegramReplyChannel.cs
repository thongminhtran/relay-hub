using Microsoft.Extensions.Logging;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Channels;
using RelayHub.Core.Domain;

namespace RelayHub.Infrastructure.Replies;

/// <summary>Telegram Bot API <c>sendMessage</c> to the chat the conversation came from, with the bot token from the source config.</summary>
public sealed class TelegramReplyChannel(HttpClient http, ILogger<TelegramReplyChannel> log) : IReplyChannel
{
    public const int MaxTextLength = 4096;

    public SourceType SourceType => SourceType.Telegram;

    public async Task<ReplyResult> SendAsync(Source source, Conversation conversation, string text, CancellationToken ct)
    {
        var token = SourceConfigs.BotToken(source);
        if (string.IsNullOrEmpty(token)) return ReplyResult.Fail("This source has no botToken configured");
        if (string.IsNullOrWhiteSpace(conversation.ExternalThreadId)) return ReplyResult.Fail("This conversation has no chat id");

        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        var body = new { chat_id = conversation.ExternalThreadId, text = ReplyHttp.Truncate(text, MaxTextLength) };

        var response = await ReplyHttp.PostJsonAsync(http, url, body, bearerToken: null, ct);
        if (response.TransportError is not null)
        {
            log.LogWarning("Telegram API unreachable for source {Source}: {Error}", source.Name, response.TransportError);
            return ReplyResult.Fail($"Telegram API unreachable: {response.TransportError}");
        }

        var json = response.Json;
        if (response.IsSuccess && ReplyHttp.GetObject(json, "result") is { } result)
            return ReplyResult.Ok(ReplyHttp.GetString(result, "message_id"), response.Status);

        var description = ReplyHttp.GetString(json, "description") ?? ReplyHttp.Truncate(response.Body, 300);
        log.LogWarning("Telegram refused the reply for source {Source}: HTTP {Status} {Description}", source.Name, response.Status, description);
        return ReplyResult.Fail($"Telegram error: {description}", response.Status);
    }
}
