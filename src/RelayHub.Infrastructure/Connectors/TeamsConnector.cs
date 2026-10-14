using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;

namespace RelayHub.Infrastructure.Connectors;

/// <summary>
/// Posts an Adaptive Card to a Microsoft Teams channel through a Workflows (Power Automate) "When a Teams webhook request is received"
/// URL — the replacement for the retired Office 365 connectors. Config: <c>{ webhookUrl }</c>.
/// </summary>
public sealed class TeamsConnector(HttpClient http) : IDestinationConnector
{
    private const int MaxTextLength = 4000;

    public DestinationType Type => DestinationType.Teams;

    public async Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        var config = ConnectorHttp.ParseConfig<TeamsConfig>(destination.ConfigJson);
        if (string.IsNullOrWhiteSpace(config?.WebhookUrl))
            return ConnectorHttp.Misconfigured("Teams destination has no webhookUrl configured");

        var payload = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[] { new { type = "TextBlock", text = ConnectorHttp.Truncate(text, MaxTextLength), wrap = true } },
                    },
                },
            },
        };
        return await ConnectorHttp.PostJsonAsync(http, config.WebhookUrl, payload, bearerToken: null, ct);
    }

    public sealed record TeamsConfig(string? WebhookUrl);
}
