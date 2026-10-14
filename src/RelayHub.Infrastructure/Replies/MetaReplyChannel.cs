using Microsoft.Extensions.Logging;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Channels;
using RelayHub.Core.Domain;

namespace RelayHub.Infrastructure.Replies;

/// <summary>
/// Meta Send API for Facebook Pages and Instagram professional accounts: <c>POST https://graph.facebook.com/v21.0/{pageOrIgId}/messages</c>
/// with the Page access token from the source config. One class, registered once per source type. The 24-hour window is checked
/// by the API before calling this; if Meta still refuses (error 10 / 2018278) the result says "Reply window closed".
/// </summary>
public sealed class MetaReplyChannel(HttpClient http, SourceType sourceType, ILogger<MetaReplyChannel> log) : IReplyChannel
{
    public const string HttpClientName = "meta-send-api";
    public const string GraphApiVersion = "v21.0";
    public const int WindowClosedErrorCode = 10;
    public const int WindowClosedErrorSubcode = 2018278;

    public SourceType SourceType => sourceType;

    public async Task<ReplyResult> SendAsync(Source source, Conversation conversation, string text, CancellationToken ct)
    {
        var token = SourceConfigs.PageAccessToken(source);
        if (string.IsNullOrEmpty(token)) return ReplyResult.Fail("This source has no pageAccessToken configured");
        if (string.IsNullOrWhiteSpace(conversation.ExternalThreadId)) return ReplyResult.Fail("This conversation has no recipient id");

        var node = string.IsNullOrWhiteSpace(source.ExternalAccountId) ? "me" : source.ExternalAccountId;
        var url = $"https://graph.facebook.com/{GraphApiVersion}/{Uri.EscapeDataString(node)}/messages";
        var body = new { recipient = new { id = conversation.ExternalThreadId }, messaging_type = "RESPONSE", message = new { text } };

        var response = await ReplyHttp.PostJsonAsync(http, url, body, token, ct);
        if (response.TransportError is not null)
        {
            log.LogWarning("Meta Send API unreachable for source {Source}: {Error}", source.Name, response.TransportError);
            return ReplyResult.Fail($"Meta Send API unreachable: {response.TransportError}");
        }

        var json = response.Json;
        if (response.IsSuccess)
            return ReplyResult.Ok(ReplyHttp.GetString(json, "message_id"), response.Status);

        var error = ReplyHttp.GetObject(json, "error");
        var code = ReplyHttp.GetString(error, "code");
        var subcode = ReplyHttp.GetString(error, "error_subcode");
        var message = ReplyHttp.GetString(error, "message") ?? ReplyHttp.Truncate(response.Body, 300);
        log.LogWarning("Meta Send API refused the reply for source {Source}: HTTP {Status} code {Code}/{Subcode} {Message}", source.Name, response.Status, code, subcode, message);

        if (code == WindowClosedErrorCode.ToString() && subcode == WindowClosedErrorSubcode.ToString())
            return ReplyResult.Fail("Reply window closed", 400);
        return ReplyResult.Fail(code is null ? $"Meta error: {message}" : $"Meta error {code}{(subcode is null ? "" : "/" + subcode)}: {message}", response.Status);
    }
}
