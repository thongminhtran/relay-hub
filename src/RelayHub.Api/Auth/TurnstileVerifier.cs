using System.Text.Json;
using Microsoft.Extensions.Options;
using RelayHub.Api.Startup;

namespace RelayHub.Api.Auth;

/// <summary>
/// Cloudflare Turnstile check for the public demo login. Only active when RelayHub:Turnstile:Secret is set; otherwise every request passes
/// (local development, tests). Fails closed on network errors: a demo token is never worth an unverified bot.
/// </summary>
public sealed class TurnstileVerifier(HttpClient http, IOptions<RelayHubOptions> options, ILogger<TurnstileVerifier> log)
{
    public const string SiteVerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public bool IsEnabled => !string.IsNullOrWhiteSpace(options.Value.Turnstile.Secret);

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        if (!IsEnabled) return true;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var form = new Dictionary<string, string> { ["secret"] = options.Value.Turnstile.Secret!, ["response"] = token };
        if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;

        try
        {
            using var response = await http.PostAsync(SiteVerifyUrl, new FormUrlEncodedContent(form), ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var ok = doc.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
            if (!ok) log.LogInformation("Turnstile rejected a demo login: {Body}", doc.RootElement.GetRawText());
            return ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Turnstile verification failed; demo login refused");
            return false;
        }
    }
}
