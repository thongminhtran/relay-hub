using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RelayHub.Api.Auth;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Conversations;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Data;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class ConversationEndpoints
{
    public sealed record ReplyRequest(string? Text);
    public sealed record ReplyResponse(long MessageId, int ConversationId, string Channel, string Status, string? ProviderMessageId, DateTimeOffset At);

    public const int MaxReplyLength = 2000;
    public const string StatusSent = "Sent";
    public const string StatusSentDemo = "SentDemo";

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/conversations").WithTags("Inbox").RequireAuthorization(AuthPolicies.Member);

        group.MapGet("/", ListAsync).WithName("ListConversations")
             .WithSummary("Conversations (one per customer per channel), newest activity first. q matches sender name, handle or message text.");
        group.MapGet("/{id:int}", GetAsync).WithName("GetConversation")
             .WithSummary("Thread (oldest → newest) plus whether a reply is possible right now (Meta: 24-hour window).");
        group.MapPost("/{id:int}/reply", ReplyAsync).WithName("ReplyToConversation")
             .WithSummary("Send a reply on the conversation's channel. Synchronous. Mocked in the Demo workspace (status SentDemo).")
             .Produces<ReplyResponse>()
             .ProducesValidationProblem()
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict)
             .ProducesProblem(StatusCodes.Status501NotImplemented)
             .ProducesProblem(StatusCodes.Status502BadGateway);

        return app;
    }

    private static async Task<IResult> ListAsync(RelayHubDbContext db, string? channel, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = from c in db.Conversations.AsNoTracking()
                    join s in db.Sources.AsNoTracking() on c.SourceId equals s.Id
                    select new { Conversation = c, Source = s };

        if (!string.IsNullOrWhiteSpace(channel))
        {
            if (!ChannelNames.TryParse(channel, out var type)) return ValidationProblem("channel", "Use web, instagram, facebook, telegram or email");
            query = query.Where(x => x.Source.Type == type);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(x =>
                (x.Conversation.SenderName != null && x.Conversation.SenderName.Contains(q))
                || (x.Conversation.SenderHandle != null && x.Conversation.SenderHandle.Contains(q))
                || db.Events.Any(e => e.ConversationId == x.Conversation.Id && e.Text.Contains(q)));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .Select(x => new
            {
                x.Conversation.Id,
                SourceType = x.Source.Type,
                x.Conversation.SourceId,
                SourceName = x.Source.Name,
                x.Conversation.SenderName,
                x.Conversation.SenderHandle,
                x.Conversation.LastInboundAt,
                x.Conversation.LastOutboundAt,
                LastMessageAt = x.Conversation.LastOutboundAt > x.Conversation.LastInboundAt ? x.Conversation.LastOutboundAt : x.Conversation.LastInboundAt,
                Last = db.Events.Where(e => e.ConversationId == x.Conversation.Id)
                    .OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id)
                    .Select(e => new { e.Text, e.Direction })
                    .FirstOrDefault(),
                MessageCount = db.Events.Count(e => e.ConversationId == x.Conversation.Id),
            })
            .OrderByDescending(x => x.LastMessageAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = items.Select(x => new
            {
                x.Id, channel = ChannelNames.For(x.SourceType), x.SourceId, x.SourceName, x.SenderName, x.SenderHandle,
                x.LastInboundAt, x.LastOutboundAt, x.LastMessageAt,
                lastText = x.Last?.Text,
                lastDirection = x.Last?.Direction.ToString().ToLowerInvariant(),
                x.MessageCount,
            }),
            page, pageSize, total,
        });
    }

    private static async Task<IResult> GetAsync(int id, RelayHubDbContext db, CancellationToken ct)
    {
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (conversation is null) return NotFound("Conversation");
        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == conversation.SourceId, ct);

        var messages = await db.Events.AsNoTracking()
            .Where(e => e.ConversationId == id)
            .OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.Direction, e.Text, At = e.ReceivedAt, e.ProviderMessageId, e.CorrelationId })
            .ToListAsync(ct);

        var reply = source is null
            ? new ReplyWindowStatus(false, null, ReplyWindow.NoReplyChannelReason)
            : ReplyWindow.Evaluate(source.Type, conversation.LastInboundAt, conversation.SenderHandle, DateTimeOffset.UtcNow);

        return Results.Ok(new
        {
            conversation.Id,
            channel = source is null ? ChannelNames.Web : ChannelNames.For(source.Type),
            conversation.SourceId, sourceName = source?.Name,
            conversation.SenderName, conversation.SenderHandle, conversation.ExternalThreadId,
            conversation.LastInboundAt, conversation.LastOutboundAt,
            reply,
            messages = messages.Select(m => new { m.Id, direction = m.Direction.ToString().ToLowerInvariant(), m.Text, m.At, m.ProviderMessageId, m.CorrelationId }),
        });
    }

    private static async Task<IResult> ReplyAsync(int id, ReplyRequest request, ClaimsPrincipal user, HttpContext http, RelayHubDbContext db,
        IEnumerable<IReplyChannel> channels, IRealtimeNotifier realtime, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? "";
        if (text.Length is 0 or > MaxReplyLength) return ValidationProblem("text", $"Text must be 1-{MaxReplyLength} characters");

        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (conversation is null) return NotFound("Conversation");
        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == conversation.SourceId, ct);
        var channel = source is null ? ChannelNames.Web : ChannelNames.For(source.Type);
        if (source is null) return ReplyUnsupported(channel, "The conversation's source no longer exists");

        var window = ReplyWindow.Evaluate(source.Type, conversation.LastInboundAt, conversation.SenderHandle, DateTimeOffset.UtcNow);
        if (!window.CanReply)
        {
            if (window.WindowClosesAt is not null)
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, type: ProblemTypes.ReplyWindowClosed, title: "Reply window closed",
                    detail: $"{ReplyWindow.MetaWindowClosedReason} (last message: {conversation.LastInboundAt:O}).");
            return ReplyUnsupported(channel, window.Reason);
        }

        string status;
        string? providerMessageId;
        if (user.IsDemo())
        {
            // Public demo: nothing leaves the system, the thread still shows the reply.
            status = StatusSentDemo;
            providerMessageId = "demo-" + Guid.NewGuid().ToString("N")[..8];
        }
        else
        {
            // One IReplyChannel per source type; web-form conversations answer by email, so they fall back to the email channel.
            var replyChannel = channels.FirstOrDefault(c => c.SourceType == source.Type)
                ?? (source.Type == SourceType.Generic ? channels.FirstOrDefault(c => c.SourceType == SourceType.Email) : null);
            if (replyChannel is null) return ReplyUnsupported(channel, null);

            var result = await SendAsync(replyChannel, source, conversation, text, loggerFactory, ct);
            if (!result.Succeeded)
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, type: ProblemTypes.ReplyFailed, title: "The channel rejected the reply",
                    detail: result.Error ?? "Unknown provider error");
            status = StatusSent;
            providerMessageId = result.ProviderMessageId;
        }

        var now = DateTimeOffset.UtcNow;
        var reply = new Event
        {
            WorkspaceId = conversation.WorkspaceId,
            SourceId = source.Id,
            ConversationId = conversation.Id,
            Direction = MessageDirection.Out,
            ExternalId = "out:" + Guid.NewGuid().ToString("N"),
            Channel = channel,
            SenderName = "You",
            Text = text,
            PayloadJson = JsonSerializer.Serialize(new { text, status, providerMessageId, to = conversation.ExternalThreadId }),
            ReceivedAt = now,
            CorrelationId = http.Items["CorrelationId"] as string ?? Guid.NewGuid().ToString("N"),
            ProviderMessageId = providerMessageId,
        };
        db.Events.Add(reply);
        conversation.LastOutboundAt = now;
        await db.SaveChangesAsync(ct); // no deliveries for outbound events

        await realtime.ReplySentAsync(reply, ct);
        return Results.Ok(new ReplyResponse(reply.Id, conversation.Id, channel, status, providerMessageId, now));
    }

    private static IResult ReplyUnsupported(string channel, string? detail)
        => Results.Problem(statusCode: StatusCodes.Status501NotImplemented, type: ProblemTypes.ReplyUnsupported, title: $"No reply channel for {channel}", detail: detail);

    /// <summary>Channels promise not to throw, but a bug in one of them must not turn into a 500 for the person who pressed Send.</summary>
    private static async Task<ReplyResult> SendAsync(IReplyChannel channel, Source source, Conversation conversation, string text, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            return await channel.SendAsync(source, conversation, text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory.CreateLogger(typeof(ConversationEndpoints)).LogError(ex, "Reply channel {Channel} threw for conversation {ConversationId}", channel.GetType().Name, conversation.Id);
            return ReplyResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
