using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Channels;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Channels;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Infrastructure.Replies;

/// <summary>What an email reply needs from the conversation's history: the last inbound subject and Message-ID (for threading) and the workspace name.</summary>
public sealed record ReplyThread(string? Subject, string? LastMessageId, string? WorkspaceName);

/// <summary>Reads <see cref="ReplyThread"/> from the database. Virtual so unit tests can stub it without a database.</summary>
public class ReplyThreadLookup(RelayHubDbContext db)
{
    public virtual async Task<ReplyThread> ForAsync(Conversation conversation, CancellationToken ct)
    {
        var workspaceName = await db.Workspaces.Where(w => w.Id == conversation.WorkspaceId).Select(w => w.Name).FirstOrDefaultAsync(ct);
        var lastInboundPayload = await db.Events
            .Where(e => e.ConversationId == conversation.Id && e.Direction == MessageDirection.In)
            .OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id)
            .Select(e => e.PayloadJson)
            .FirstOrDefaultAsync(ct);

        string? subject = null, messageId = null;
        if (lastInboundPayload is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(lastInboundPayload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    subject = Str(doc.RootElement, "subject");
                    messageId = Str(doc.RootElement, "messageId");
                }
            }
            catch (JsonException) { /* a payload we did not write; reply without threading */ }
        }
        return new ReplyThread(subject, messageId, workspaceName);

        static string? Str(JsonElement o, string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
    }
}

/// <summary>
/// Sends the reply as an email through Resend. Serves Email sources (to = the conversation's address) and, registered a second time,
/// Generic sources whose conversation has a sender email (website form). From = the source's <c>fromAddress</c> or <c>RelayHub:Resend:From</c>.
/// Subject = "Re: &lt;last inbound subject&gt;" when known, else "Reply from &lt;workspace&gt;"; In-Reply-To keeps the customer's thread intact.
/// </summary>
public sealed class EmailReplyChannel(
    HttpClient http,
    IOptions<ChannelOptions> options,
    ReplyThreadLookup threads,
    SourceType sourceType,
    ILogger<EmailReplyChannel> log) : IReplyChannel
{
    public const string HttpClientName = "resend";
    public const string ResendEmailsUrl = "https://api.resend.com/emails";

    public SourceType SourceType => sourceType;

    public async Task<ReplyResult> SendAsync(Source source, Conversation conversation, string text, CancellationToken ct)
    {
        var to = sourceType == SourceType.Generic ? conversation.SenderHandle : conversation.ExternalThreadId;
        if (!SourceConfigs.IsEmail(to)) return ReplyResult.Fail("This conversation has no email address to reply to");

        var apiKey = options.Value.Resend.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return ReplyResult.Fail("Email replies are not configured (RelayHub:Resend:ApiKey)");
        var from = SourceConfigs.FromAddress(source) ?? options.Value.Resend.From;
        if (string.IsNullOrWhiteSpace(from)) return ReplyResult.Fail("No sender address: set RelayHub:Resend:From or the source's fromAddress");

        var thread = await threads.ForAsync(conversation, ct);
        var subject = thread.Subject is { } s
            ? s.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? s : "Re: " + s
            : $"Reply from {thread.WorkspaceName ?? "RelayHub"}";
        var headers = thread.LastMessageId is { } id ? new Dictionary<string, string> { ["In-Reply-To"] = id, ["References"] = id } : null;
        var body = new { from, to = new[] { to }, subject, text, headers };

        var response = await ReplyHttp.PostJsonAsync(http, ResendEmailsUrl, body, apiKey, ct);
        if (response.TransportError is not null)
        {
            log.LogWarning("Resend unreachable for source {Source}: {Error}", source.Name, response.TransportError);
            return ReplyResult.Fail($"Resend unreachable: {response.TransportError}");
        }

        var json = response.Json;
        if (response.IsSuccess) return ReplyResult.Ok(ReplyHttp.GetString(json, "id"), response.Status);

        var message = ReplyHttp.GetString(json, "message") ?? ReplyHttp.Truncate(response.Body, 300);
        log.LogWarning("Resend refused the reply for source {Source}: HTTP {Status} {Message}", source.Name, response.Status, message);
        return ReplyResult.Fail($"Resend error: {message}", response.Status);
    }
}
