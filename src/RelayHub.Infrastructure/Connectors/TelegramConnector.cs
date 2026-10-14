using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;

namespace RelayHub.Infrastructure.Connectors;

/// <summary>Sends the rendered notification to a Telegram chat (the owner's DM with their bot, or a group) via <c>sendMessage</c>. Config: <c>{ botToken, chatId }</c>.</summary>
public sealed class TelegramConnector(HttpClient http) : IDestinationConnector
{
    private const int TelegramMaxTextLength = 4096;

    public DestinationType Type => DestinationType.Telegram;

    public async Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        var config = ConnectorHttp.ParseConfig<TelegramConfig>(destination.ConfigJson);
        if (string.IsNullOrWhiteSpace(config?.BotToken) || string.IsNullOrWhiteSpace(config.ChatId))
            return ConnectorHttp.Misconfigured("Telegram destination needs botToken and chatId");

        var url = $"https://api.telegram.org/bot{config.BotToken}/sendMessage";
        var body = new { chat_id = config.ChatId, text = ConnectorHttp.Truncate(text, TelegramMaxTextLength) };
        return await ConnectorHttp.PostJsonAsync(http, url, body, bearerToken: null, ct);
    }

    public sealed record TelegramConfig(string? BotToken, string? ChatId);
}
