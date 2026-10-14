using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Infrastructure.Processing.Parsers;

/// <summary>
/// <c>POST /api/v1/hooks/{sourceKey}</c> with an API key (website form, custom sources). The key was verified by the API; here we
/// enforce what needs the database: the source exists, is enabled, and the URL's source key names it — so revocation takes
/// effect at processing time. Payload: <c>{ name?, email?, message? | text? }</c>; the thread is the email (or the name).
/// </summary>
public sealed class GenericParser(RelayHubDbContext db, ILogger<GenericParser> log) : ISourceParser
{
    public string Kind => IncomingKinds.Generic;

    public async Task<ParseResult> ParseAsync(IncomingMessage msg, CancellationToken ct)
    {
        var source = await db.Sources.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.KeyId == msg.KeyId, ct);
        if (source is null || !source.IsEnabled || !string.Equals(source.Name, msg.SourceKey, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Incoming message dropped: key {KeyId} does not belong to an enabled source named {SourceKey}", msg.KeyId, msg.SourceKey);
            return ParseResult.Dropped($"key {msg.KeyId} does not belong to an enabled source named {msg.SourceKey}");
        }
        return Parse(msg, source);
    }

    public static ParseResult Parse(IncomingMessage msg, Source source)
    {
        if (ParserJson.ParseObject(msg.PayloadJson) is not { } payload)
            return ParseResult.Dropped($"payload is not a JSON object (source {source.Name})");

        var name = payload.GetString("name") ?? "Unknown";
        var email = payload.GetString("email");
        var text = payload.GetString("message") ?? payload.GetString("text") ?? "";
        var threadKey = (email ?? name).Trim().ToLowerInvariant();

        return ParseResult.One(new ParsedInbound(
            source, msg.ExternalId, threadKey, name, email, text, payload.GetRawText(), msg.ReceivedAt,
            Channel: source.Type == SourceType.Generic ? "web" : source.Type.ToString().ToLowerInvariant()));
    }
}
