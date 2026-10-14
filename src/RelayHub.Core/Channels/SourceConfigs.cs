using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayHub.Core.Domain;

namespace RelayHub.Core.Channels;

/// <summary>
/// Per-type rules for <see cref="Source.ConfigJson"/>: what a source of each type needs to receive and reply
/// (Meta page token, Telegram bot token, ...). <see cref="Validate"/> is called by the sources API on create/update and
/// returns the normalized JSON to store; the typed accessors are what the reply channels read at send time.
/// </summary>
public static partial class SourceConfigs
{
    [GeneratedRegex(@"^\d+:[A-Za-z0-9_-]{30,}$")]
    private static partial Regex TelegramBotToken();

    /// <summary>Validates and normalizes a source config. Unknown keys are dropped; secrets are stored as given (masked on the way out).</summary>
    public static (string? ConfigJson, string? Error) Validate(SourceType type, JsonElement? config)
    {
        if (config is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
            return (null, "Config must be a JSON object");
        var cfg = config is { ValueKind: JsonValueKind.Object } c ? c : default(JsonElement?);

        switch (type)
        {
            case SourceType.Generic:
                return ("{}", null);

            case SourceType.Instagram:
            case SourceType.Facebook:
            {
                var token = GetString(cfg, "pageAccessToken");
                if (string.IsNullOrWhiteSpace(token))
                    return (null, "pageAccessToken is required: a Page access token with pages_messaging (and instagram_manage_messages for Instagram)");
                var pageName = GetString(cfg, "pageName");
                return (JsonSerializer.Serialize(new { pageAccessToken = token.Trim(), pageName = Trimmed(pageName) }), null);
            }

            case SourceType.Telegram:
            {
                var token = GetString(cfg, "botToken")?.Trim();
                if (!IsTelegramBotToken(token))
                    return (null, "botToken must be a Telegram bot token from @BotFather, e.g. 123456789:AAH...");
                return (JsonSerializer.Serialize(new { botToken = token }), null);
            }

            case SourceType.Email:
            {
                var from = Trimmed(GetString(cfg, "fromAddress"));
                if (from is not null && !IsEmail(from))
                    return (null, "fromAddress must be a valid email address");
                return (JsonSerializer.Serialize(new { fromAddress = from }), null);
            }

            default:
                return (null, $"Unknown source type {type}");
        }
    }

    /// <summary>Meta Page access token (Facebook and Instagram sources).</summary>
    public static string? PageAccessToken(Source source) => Read(source, "pageAccessToken");

    public static string? PageName(Source source) => Read(source, "pageName");

    /// <summary>Telegram bot token (Telegram sources).</summary>
    public static string? BotToken(Source source) => Read(source, "botToken");

    /// <summary>Sender address for email replies (Email sources); null = use the RelayHub-wide default.</summary>
    public static string? FromAddress(Source source) => Read(source, "fromAddress");

    /// <summary><c>123456789:AAH...</c> — numeric bot id, colon, 30+ token characters.</summary>
    public static bool IsTelegramBotToken(string? value) => value is not null && TelegramBotToken().IsMatch(value);

    public static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value.Any(char.IsWhiteSpace)) return false;
        return MailAddress.TryCreate(value, out var address) && address.Address == value && value.IndexOf('@') > 0;
    }

    private static string? Read(Source source, string property)
    {
        if (string.IsNullOrWhiteSpace(source.ConfigJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(source.ConfigJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Trimmed(GetString(doc.RootElement, property)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement? obj, string property)
        => obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
