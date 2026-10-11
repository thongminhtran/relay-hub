using System.Net.Http.Json;
using System.Text.Json;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;

namespace RelayHub.Infrastructure.Connectors;

/// <summary>Posts a message to a Discord channel through an incoming-webhook URL.</summary>
public sealed class DiscordConnector(HttpClient http) : IDestinationConnector
{
    private const int DiscordMaxContentLength = 2000;

    public DestinationType Type => DestinationType.Discord;

    public async Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        var config = ParseConfig(destination.ConfigJson);
        if (string.IsNullOrWhiteSpace(config?.WebhookUrl))
            return new DeliveryResult(false, null, "Discord destination has no webhookUrl configured", Retryable: false);

        var body = new { content = Truncate(text, DiscordMaxContentLength), username = config.Username ?? "RelayHub" };

        using var response = await http.PostAsJsonAsync(config.WebhookUrl, body, ct);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode) return DeliveryResult.Ok(status);

        var detail = Truncate(await response.Content.ReadAsStringAsync(ct), 300);
        var retryable = status == 429 || status >= 500;
        return new DeliveryResult(false, status, $"{status} {response.ReasonPhrase}: {detail}", retryable);
    }

    private static DiscordConfig? ParseConfig(string json)
    {
        try { return JsonSerializer.Deserialize<DiscordConfig>(json, JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public sealed record DiscordConfig(string? WebhookUrl, string? Username);
}
