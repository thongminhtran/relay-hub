using System.Text.Json;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;

namespace RelayHub.Infrastructure.Processing.Parsers;

/// <summary>
/// One inbound message, normalised: what <see cref="IncomingProcessor"/> needs to upsert the Conversation and store the Event.
/// A single queue message can yield several of these (a Meta batch), or none (read receipts, echoes).
/// </summary>
/// <param name="Source">The source the message belongs to (resolved by the parser; sets the workspace).</param>
/// <param name="ExternalId">Provider message id — unique per source, the dedupe key.</param>
/// <param name="ExternalThreadId">Who the conversation is with: PSID / IGSID, Telegram chat id, email address, form email or name.</param>
/// <param name="Channel">Lower-case channel name stored on the Event: web, facebook, instagram, telegram, email.</param>
public sealed record ParsedInbound(
    Source Source,
    string ExternalId,
    string ExternalThreadId,
    string? SenderName,
    string? SenderHandle,
    string Text,
    string PayloadJson,
    DateTimeOffset At,
    string Channel);

/// <summary>Zero or more parsed messages. <see cref="DropReason"/> set = the whole queue message is unusable (unknown source, bad payload).</summary>
public sealed record ParseResult(IReadOnlyList<ParsedInbound> Items, string? DropReason = null)
{
    /// <summary>Valid payload that carries nothing to store (echo, read receipt, unsupported update type).</summary>
    public static readonly ParseResult Nothing = new([]);

    public static ParseResult Dropped(string reason) => new([], reason);

    public static ParseResult One(ParsedInbound item) => new([item]);
}

/// <summary>
/// Turns the raw payload of one <see cref="IncomingMessage"/> into <see cref="ParsedInbound"/> items (Strategy: one parser per
/// <see cref="IncomingMessage.Kind"/>). Parsers resolve the source from the database and must never throw for a bad payload —
/// they answer <see cref="ParseResult.Dropped"/> instead (a throw would poison the queue message).
/// </summary>
public interface ISourceParser
{
    /// <summary>One of <see cref="IncomingKinds"/>.</summary>
    string Kind { get; }

    Task<ParseResult> ParseAsync(IncomingMessage msg, CancellationToken ct);
}

internal static class ParserJson
{
    public static JsonElement? ParseObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? GetString(this JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Numbers and numeric strings (Meta sends ids as strings, Telegram as numbers).</summary>
    public static string? GetIdString(this JsonElement obj, string property)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(property, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    public static JsonElement? GetObject(this JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>Elements of an array property; empty when the property is missing or not an array.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static bool GetBool(this JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    public static long? GetInt64(this JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    public static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    /// <summary>Provider timestamps arrive as Unix seconds (Telegram) or milliseconds (Meta); anything unparseable falls back.</summary>
    public static DateTimeOffset FromUnix(long? value, bool milliseconds, DateTimeOffset fallback)
    {
        if (value is not { } v || v <= 0) return fallback;
        try
        {
            var at = milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(v) : DateTimeOffset.FromUnixTimeSeconds(v);
            // A provider clock far in the future would push the reply window the wrong way; trust our own clock then.
            return at > fallback.AddMinutes(5) ? fallback : at;
        }
        catch (ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }
}
