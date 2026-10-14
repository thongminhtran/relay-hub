using System.Text.Json;
using RelayHub.Core.Domain;

namespace RelayHub.Core.Channels;

/// <summary>
/// Per-source-type config rules for <c>Source.ConfigJson</c> (e.g. {"pageAccessToken": "..."} for Meta, {"botToken": "..."} for Telegram).
/// Week 3 keeps this to "a JSON object or nothing" and stores the object as given; the Week 4 channel work adds the per-type required keys.
/// </summary>
public static class SourceConfigs
{
    /// <returns>The normalized JSON to store (never null when <c>Error</c> is null), or an error message for the <c>config</c> field.</returns>
    public static (string? ConfigJson, string? Error) Validate(SourceType type, JsonElement? config)
    {
        if (config is not { } cfg || cfg.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return ("{}", null);
        if (cfg.ValueKind != JsonValueKind.Object) return (null, "Config must be a JSON object");
        return (cfg.GetRawText(), null);
    }
}
