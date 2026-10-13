using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelayHub.Core.Abstractions;

namespace RelayHub.Infrastructure.Alerts;

public sealed class OpsAlertOptions
{
    public const string SectionName = "RelayHub";

    /// <summary>Discord incoming-webhook URL for operator alerts (dead-letter, poison messages). Optional; secret, never in the repo.</summary>
    public string? OpsDiscordWebhookUrl { get; set; }
}

/// <summary>"Borrowed push notification" for the operator: dead-letters and poison messages land in Tim's Discord.</summary>
public sealed class DiscordOpsAlerts(HttpClient http, IOptions<OpsAlertOptions> options, ILogger<DiscordOpsAlerts> log) : IOpsAlerts
{
    private const int DiscordMaxContentLength = 2000;

    public async Task NotifyAsync(string title, string detail, CancellationToken ct)
    {
        var url = options.Value.OpsDiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            log.LogWarning("Ops alert (RelayHub:OpsDiscordWebhookUrl not configured): {Title} — {Detail}", title, detail);
            return;
        }

        try
        {
            var content = $"🚨 **{title}**\n{detail}";
            if (content.Length > DiscordMaxContentLength) content = content[..(DiscordMaxContentLength - 1)] + "…";
            using var response = await http.PostAsJsonAsync(url, new { content, username = "RelayHub ops" }, ct);
            if (!response.IsSuccessStatusCode)
                log.LogWarning("Ops alert to Discord failed with HTTP {Status}: {Title}", (int)response.StatusCode, title);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Alerts run inside queue processing: they must never poison the message they report about.
            log.LogWarning(ex, "Ops alert to Discord failed: {Title}", title);
        }
    }
}
