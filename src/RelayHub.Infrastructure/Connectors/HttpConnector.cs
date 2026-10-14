using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Channels;
using RelayHub.Core.Domain;
using RelayHub.Core.Templates;

namespace RelayHub.Infrastructure.Connectors;

/// <summary>
/// Generic outbound webhook for owners who run their own systems. Config: <c>{ url, method ("POST"|"PUT"), headers?: {k:v}, bodyTemplate? }</c>.
/// https only, redirects are not followed, and every send passes <see cref="SsrfGuard"/> twice: once here (clear error in the attempt
/// history) and once in <see cref="ConnectGuardedAsync"/>, which pins the TCP connection to the vetted public address so a DNS answer
/// that changes between the two cannot reach a private network. <c>bodyTemplate</c> takes the route tokens plus <c>{{eventId}}</c> and
/// <c>{{correlationId}}</c>; values are JSON-escaped when the template is JSON. Without a template a JSON summary of the event is sent.
/// </summary>
public sealed class HttpConnector(HttpClient http) : IDestinationConnector
{
    public static readonly string[] AllowedMethods = ["POST", "PUT"];

    /// <summary>Headers the owner may not override: they describe the connection, not the payload.</summary>
    public static readonly string[] ReservedHeaders = ["host", "content-length", "transfer-encoding", "connection", "expect", "upgrade", "te", "trailer"];

    public DestinationType Type => DestinationType.Http;

    public async Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        var config = ConnectorHttp.ParseConfig<HttpConfig>(destination.ConfigJson);
        if (config is null || string.IsNullOrWhiteSpace(config.Url))
            return ConnectorHttp.Misconfigured("HTTP destination has no url configured");
        var method = (config.Method ?? "POST").ToUpperInvariant();
        if (!AllowedMethods.Contains(method))
            return ConnectorHttp.Misconfigured($"HTTP destination method must be one of {string.Join(", ", AllowedMethods)}");

        // Guard 1: the destination may have been saved days ago; its host may now resolve somewhere else.
        var verdict = await SsrfGuard.ResolveAsync(config.Url, ct);
        if (!verdict.Allowed)
            return new DeliveryResult(false, null, $"Refused by the SSRF guard: {verdict.Error}", Retryable: verdict.Transient);

        var (body, contentType) = RenderBody(config.BodyTemplate, text, evt);
        using var request = new HttpRequestMessage(new HttpMethod(method), config.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        request.Headers.UserAgent.ParseAdd("RelayHub/1.0");
        foreach (var (name, value) in config.Headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(name) || ReservedHeaders.Contains(name.ToLowerInvariant())) continue;
            if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.Remove("Content-Type");
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await http.SendAsync(request, ct);
        if ((int)response.StatusCode is >= 300 and < 400)
            return new DeliveryResult(false, (int)response.StatusCode, $"{(int)response.StatusCode} redirect to {response.Headers.Location}: redirects are not followed", Retryable: false);
        return await ConnectorHttp.ToResultAsync(response, ct);
    }

    /// <summary>Guard 2, wired as the <c>SocketsHttpHandler.ConnectCallback</c> of this connector's HttpClient: resolve, vet, connect to the vetted address only.</summary>
    public static async ValueTask<Stream> ConnectGuardedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var verdict = await SsrfGuard.ResolveHostAsync(context.DnsEndPoint.Host, ct);
        if (!verdict.Allowed) throw new HttpRequestException($"Refused by the SSRF guard: {verdict.Error}");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(verdict.Addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static (string Body, string ContentType) RenderBody(string? template, string text, Event evt)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            var summary = new
            {
                eventId = evt.Id, correlationId = evt.CorrelationId, channel = evt.Channel, sourceId = evt.SourceId,
                name = evt.SenderName, email = evt.SenderHandle, text, receivedAt = evt.ReceivedAt,
            };
            return (JsonSerializer.Serialize(summary, JsonSerializerOptions.Web), "application/json");
        }

        var trimmed = template.TrimStart();
        var isJson = trimmed.StartsWith('{') || trimmed.StartsWith('[');
        var values = new Dictionary<string, string?>
        {
            ["name"] = evt.SenderName, ["email"] = evt.SenderHandle, ["handle"] = evt.SenderHandle, ["text"] = text,
            ["channel"] = evt.Channel, ["receivedAt"] = evt.ReceivedAt.ToString("u"),
            ["eventId"] = evt.Id.ToString(), ["correlationId"] = evt.CorrelationId,
        };
        if (isJson)
        {
            // The template is JSON with tokens inside string literals: escape each value so quotes / newlines keep the document valid.
            values = values.ToDictionary(kv => kv.Key, string? (kv) => kv.Value is null ? "" : JsonEncodedText.Encode(kv.Value).ToString());
        }
        return (TemplateRenderer.Render(template, values), isJson ? "application/json" : "text/plain");
    }

    public sealed record HttpConfig(string? Url, string? Method, Dictionary<string, string>? Headers, string? BodyTemplate);
}
