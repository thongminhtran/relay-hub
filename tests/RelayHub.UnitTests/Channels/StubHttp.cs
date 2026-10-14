using System.Net;
using System.Text;
using System.Text.Json;

namespace RelayHub.UnitTests.Channels;

/// <summary>Handler-based fake for HttpClient: records every request (URL, method, headers, body) and answers from a script. No network.</summary>
public sealed class StubHttp : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, Uri? Url, string? Authorization, string ContentType, string Body, Dictionary<string, string> Headers)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public StubHttp(HttpStatusCode status, object? body = null) : this(_ => Json(status, body)) { }

    public List<Call> Calls { get; } = [];

    public Call Single => Assert.Single(Calls);

    public HttpClient Client => new(this) { Timeout = TimeSpan.FromSeconds(10) };

    public static HttpResponseMessage Json(HttpStatusCode status, object? body)
        => new(status) { Content = new StringContent(body is null ? "" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static StubHttp Throwing(Exception ex) => new(_ => throw ex);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Calls.Add(new Call(request.Method, request.RequestUri, request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.ToString() ?? "", body, headers));
        return _respond(request);
    }
}
