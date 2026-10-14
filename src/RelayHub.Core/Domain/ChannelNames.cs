namespace RelayHub.Core.Domain;

/// <summary>
/// The channel string stored on <see cref="Event.Channel"/> and shown in the inbox, derived from the source type:
/// generic sources are "web" (the website form), every other type is its lower-case name (instagram, facebook, telegram, email).
/// </summary>
public static class ChannelNames
{
    public const string Web = "web";

    public static string For(SourceType type) => type == SourceType.Generic ? Web : type.ToString().ToLowerInvariant();

    /// <summary>Inverse of <see cref="For"/>, for query-string filters ("web" → Generic). Case-insensitive.</summary>
    public static bool TryParse(string? channel, out SourceType type)
    {
        if (string.Equals(channel, Web, StringComparison.OrdinalIgnoreCase)) { type = SourceType.Generic; return true; }
        if (Enum.TryParse(channel, ignoreCase: true, out type) && Enum.IsDefined(type) && type != SourceType.Generic) return true;
        type = default;
        return false;
    }
}
