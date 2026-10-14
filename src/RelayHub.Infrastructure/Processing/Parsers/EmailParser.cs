using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Infrastructure.Processing.Parsers;

/// <summary>
/// One inbound email as posted by the Cloudflare Email Worker: <c>{ to, from, fromName?, subject?, text, html?, messageId, inReplyTo?, receivedAt? }</c>.
/// The source is the enabled Email source whose ExternalAccountId is the <c>to</c> address (case-insensitive); when the workspace has
/// exactly one enabled Email source it is used for any address (catch-all). The conversation is the sender's address.
/// </summary>
public sealed partial class EmailParser(RelayHubDbContext db, ILogger<EmailParser> log) : ISourceParser
{
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTags();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    public string Kind => IncomingKinds.Email;

    public async Task<ParseResult> ParseAsync(IncomingMessage msg, CancellationToken ct)
    {
        if (ParserJson.ParseObject(msg.PayloadJson) is not { } mail)
            return ParseResult.Dropped("email payload is not a JSON object");

        var to = AddressOf(mail.GetString("to"));
        var sources = await db.Sources.IgnoreQueryFilters().Where(s => s.Type == SourceType.Email && s.IsEnabled).ToListAsync(ct);
        var source = to is null ? null : sources.FirstOrDefault(s => string.Equals(s.ExternalAccountId, to, StringComparison.OrdinalIgnoreCase));
        source ??= sources.Count == 1 ? sources[0] : null;
        if (source is null)
        {
            var reason = sources.Count == 0 ? "no enabled Email source exists" : $"no enabled Email source has ExternalAccountId {to ?? "(missing to)"} and {sources.Count} exist";
            log.LogWarning("Inbound email dropped: {Reason}", reason);
            return ParseResult.Dropped(reason);
        }
        return Parse(msg, source);
    }

    public static ParseResult Parse(IncomingMessage msg, Source source)
    {
        if (ParserJson.ParseObject(msg.PayloadJson) is not { } mail)
            return ParseResult.Dropped($"email payload is not a JSON object (source {source.Name})");

        var fromRaw = mail.GetString("from");
        var from = AddressOf(fromRaw);
        if (from is null) return ParseResult.Dropped($"email has no valid from address (source {source.Name})");

        var fromName = mail.GetString("fromName")?.Trim();
        if (string.IsNullOrEmpty(fromName)) fromName = DisplayNameOf(fromRaw) ?? from[..from.IndexOf('@')];

        var subject = mail.GetString("subject")?.Trim() ?? "";
        var body = mail.GetString("text")?.Trim();
        if (string.IsNullOrEmpty(body) && mail.GetString("html") is { } html) body = StripHtml(html);
        body ??= "";
        var text = subject.Length == 0 ? body : body.Length == 0 ? subject : subject + "\n\n" + body;

        var at = DateTimeOffset.TryParse(mail.GetString("receivedAt"), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                 && parsed <= msg.ReceivedAt.AddMinutes(5) ? parsed : msg.ReceivedAt;

        var externalId = string.IsNullOrWhiteSpace(msg.ExternalId) ? mail.GetString("messageId") : msg.ExternalId;
        if (string.IsNullOrWhiteSpace(externalId)) return ParseResult.Dropped($"email has no messageId (source {source.Name})");

        return ParseResult.One(new ParsedInbound(
            source,
            ExternalId: ParserJson.Clip(externalId, 200),
            ExternalThreadId: ParserJson.Clip(from, 200),
            SenderName: ParserJson.Clip(fromName, 200),
            SenderHandle: ParserJson.Clip(from, 200),
            Text: text,
            PayloadJson: mail.GetRawText(),
            At: at,
            Channel: "email"));
    }

    /// <summary>The bare address of <c>Name &lt;addr@host&gt;</c> or <c>addr@host</c>, lower-cased; null when it is not an address.</summary>
    public static string? AddressOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (!MailAddress.TryCreate(value, out var address) || string.IsNullOrEmpty(address.Address) || !address.Address.Contains('@')) return null;
        return address.Address.ToLowerInvariant();
    }

    private static string? DisplayNameOf(string? value)
        => !string.IsNullOrWhiteSpace(value) && MailAddress.TryCreate(value.Trim(), out var a) && !string.IsNullOrWhiteSpace(a.DisplayName) ? a.DisplayName.Trim() : null;

    private static string StripHtml(string html)
    {
        var text = HtmlTags().Replace(html.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n").Replace("</p>", "\n\n"), "");
        return BlankLines().Replace(WebUtility.HtmlDecode(text), "\n\n").Trim();
    }
}
