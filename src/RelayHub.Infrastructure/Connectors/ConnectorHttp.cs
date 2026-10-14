using System.Text;
using System.Text.Json;
using RelayHub.Core.Abstractions;

namespace RelayHub.Infrastructure.Connectors;

/// <summary>Shared plumbing for the notification connectors: config parsing, one JSON POST, and the 429 / 5xx = retryable rule.</summary>
internal static class ConnectorHttp
{
    public static T? ParseConfig<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }

    public static DeliveryResult Misconfigured(string error) => new(false, null, error, Retryable: false);

    /// <summary>Posts <paramref name="body"/> as JSON; transport errors propagate to <c>DeliveryDispatcher</c>, which records them as retryable.</summary>
    public static async Task<DeliveryResult> PostJsonAsync(HttpClient http, string url, object body, string? bearerToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(bearerToken)) request.Headers.Authorization = new("Bearer", bearerToken);
        using var response = await http.SendAsync(request, ct);
        return await ToResultAsync(response, ct);
    }

    public static async Task<DeliveryResult> ToResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode) return DeliveryResult.Ok(status);

        var detail = Truncate(await response.Content.ReadAsStringAsync(ct), 300);
        var retryable = status == 429 || status >= 500;
        return new DeliveryResult(false, status, $"{status} {response.ReasonPhrase}: {detail}", retryable);
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
