using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RelayHub.Infrastructure.Replies;

/// <summary>One provider call: JSON in, (status, body) out, transport failures turned into an error string. Shared by the reply channels.</summary>
internal static class ReplyHttp
{
    public sealed record Response(int? Status, string Body, string? TransportError)
    {
        public bool IsSuccess => Status is >= 200 and < 300;

        public JsonElement? Json
        {
            get
            {
                try
                {
                    using var doc = JsonDocument.Parse(Body);
                    return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }
    }

    public static async Task<Response> PostJsonAsync(HttpClient http, string url, object body, string? bearerToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(bearerToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return new Response((int)response.StatusCode, text, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return new Response(null, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static string? GetString(JsonElement? obj, string property)
        => obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(property, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    public static JsonElement? GetObject(JsonElement? obj, string property)
        => obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
