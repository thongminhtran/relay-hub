using Microsoft.Extensions.Options;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Templates;
using RelayHub.Infrastructure.Channels;

namespace RelayHub.Infrastructure.Connectors;

/// <summary>
/// Emails the rendered notification through Resend. Config: <c>{ to, from?, subject? }</c>; <c>from</c> defaults to
/// <c>RelayHub:Resend:From</c>, <c>subject</c> may use the route template tokens (<c>{{name}} {{channel}} {{email}} {{receivedAt}}</c>).
/// </summary>
public sealed class EmailConnector(HttpClient http, IOptions<ChannelOptions> options) : IDestinationConnector
{
    public const string ResendEmailsUrl = "https://api.resend.com/emails";
    private const string DefaultSubject = "New message from {{name}} via {{channel}}";

    public DestinationType Type => DestinationType.Email;

    public async Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        var config = ConnectorHttp.ParseConfig<EmailConfig>(destination.ConfigJson);
        if (string.IsNullOrWhiteSpace(config?.To))
            return ConnectorHttp.Misconfigured("Email destination has no recipient (to) configured");

        var apiKey = options.Value.Resend.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return ConnectorHttp.Misconfigured("Email destinations need RelayHub:Resend:ApiKey");
        var from = string.IsNullOrWhiteSpace(config.From) ? options.Value.Resend.From : config.From;
        if (string.IsNullOrWhiteSpace(from))
            return ConnectorHttp.Misconfigured("Email destination has no sender: set RelayHub:Resend:From or the destination's from");

        var subjectValues = new Dictionary<string, string?>
        {
            ["name"] = evt.SenderName ?? "a customer",
            ["email"] = evt.SenderHandle,
            ["handle"] = evt.SenderHandle,
            ["channel"] = evt.Channel,
            ["receivedAt"] = evt.ReceivedAt.ToString("u"),
        };
        var subject = TemplateRenderer.Render(string.IsNullOrWhiteSpace(config.Subject) ? DefaultSubject : config.Subject, subjectValues).Trim();
        if (subject.Length == 0) subject = "New message";

        var body = new { from, to = new[] { config.To }, subject = ConnectorHttp.Truncate(subject, 200), text };
        return await ConnectorHttp.PostJsonAsync(http, ResendEmailsUrl, body, apiKey, ct);
    }

    public sealed record EmailConfig(string? To, string? From, string? Subject);
}
