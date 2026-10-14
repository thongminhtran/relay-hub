using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;
using RelayHub.Infrastructure.Processing.Parsers;

namespace RelayHub.Infrastructure.Processing;

public enum IncomingOutcome
{
    /// <summary>At least one event stored, deliveries created and queued.</summary>
    Stored,
    /// <summary>Same source + ExternalId already stored (webhook retry or queue redelivery). Nothing changed.</summary>
    Duplicate,
    /// <summary>Unknown / disabled source, source name mismatch or unusable payload. Logged and discarded.</summary>
    Dropped,
    /// <summary>Valid payload with nothing to store: Meta echo / read receipt / delivery report, a Telegram edit, an unsubscribed topic.</summary>
    Ignored,
}

/// <summary>
/// Worker side of ingest: picks the parser for <see cref="IncomingMessage.Kind"/> (generic form, Meta batch, Telegram update, email),
/// then for every parsed message upserts the Conversation, stores the Event, creates one Pending Delivery per route and queues
/// each of them. Safe to run more than once for the same message (unique index on SourceId + ExternalId). Bad payloads are
/// Dropped, never thrown: a throw would send the queue message to the poison queue, which is reserved for infrastructure failures.
/// </summary>
public sealed class IncomingProcessor(
    RelayHubDbContext db,
    AmbientWorkspace ambient,
    DeliveryDispatcher dispatcher,
    IRelayQueues queues,
    IEnumerable<ISourceParser> parsers,
    IRealtimeNotifier notifier,
    ILogger<IncomingProcessor> log)
{
    private const int MaxTextLength = 4000;

    public async Task<IncomingOutcome> ProcessAsync(IncomingMessage msg, CancellationToken ct)
    {
        // Parsers look sources up across workspaces (a webhook carries no workspace); each stored item then scopes to its source.
        ambient.WorkspaceId = null;

        var parser = parsers.FirstOrDefault(p => string.Equals(p.Kind, msg.Kind, StringComparison.OrdinalIgnoreCase));
        if (parser is null)
        {
            log.LogWarning("Incoming message dropped: no parser for kind {Kind} (source key {SourceKey})", msg.Kind, msg.SourceKey);
            return IncomingOutcome.Dropped;
        }

        ParseResult parsed;
        try
        {
            parsed = await parser.ParseAsync(msg, ct);
        }
        catch (Exception ex) when (ex is not (DbException or DbUpdateException or OperationCanceledException))
        {
            // Safety net under the parsers' own checks: a payload shape nobody anticipated must not poison the queue.
            log.LogError(ex, "Incoming {Kind} message dropped: parser failed (source key {SourceKey}, externalId {ExternalId})", msg.Kind, msg.SourceKey, msg.ExternalId);
            return IncomingOutcome.Dropped;
        }

        if (parsed.Items.Count == 0)
        {
            if (parsed.DropReason is not null)
            {
                log.LogWarning("Incoming {Kind} message dropped: {Reason}", msg.Kind, parsed.DropReason);
                return IncomingOutcome.Dropped;
            }
            log.LogInformation("Incoming {Kind} message carried nothing to store (source key {SourceKey})", msg.Kind, msg.SourceKey);
            return IncomingOutcome.Ignored;
        }

        var stored = 0;
        var duplicates = 0;
        foreach (var item in parsed.Items)
        {
            if (await StoreAsync(item, msg, ct) == IncomingOutcome.Stored) stored++;
            else duplicates++;
        }
        if (parsed.Items.Count > 1)
            log.LogInformation("Incoming {Kind} batch: {Stored} stored, {Duplicates} duplicate(s)", msg.Kind, stored, duplicates);
        return stored > 0 ? IncomingOutcome.Stored : IncomingOutcome.Duplicate;
    }

    /// <summary>Conversation upsert + Event + deliveries for one parsed message. Leaves the change tracker empty so the next item starts clean.</summary>
    private async Task<IncomingOutcome> StoreAsync(ParsedInbound item, IncomingMessage msg, CancellationToken ct)
    {
        var source = item.Source;
        ambient.WorkspaceId = source.WorkspaceId;

        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.SourceId == source.Id && c.ExternalThreadId == item.ExternalThreadId, ct);
        if (conversation is null)
        {
            conversation = new Conversation
            {
                WorkspaceId = source.WorkspaceId, SourceId = source.Id, ExternalThreadId = item.ExternalThreadId,
                SenderName = item.SenderName, SenderHandle = item.SenderHandle,
            };
            db.Conversations.Add(conversation);
        }
        else
        {
            // A later message may carry what the first one lacked (a Telegram user who set a username, a form that now has an email).
            conversation.SenderName ??= item.SenderName;
            conversation.SenderHandle ??= item.SenderHandle;
        }
        if (conversation.LastInboundAt is null || item.At > conversation.LastInboundAt) conversation.LastInboundAt = item.At;

        var evt = new Event
        {
            WorkspaceId = source.WorkspaceId,
            SourceId = source.Id,
            ExternalId = item.ExternalId,
            Channel = item.Channel,
            SenderName = item.SenderName,
            SenderHandle = item.SenderHandle,
            Text = item.Text.Length > MaxTextLength ? item.Text[..MaxTextLength] : item.Text,
            PayloadJson = item.PayloadJson,
            ReceivedAt = item.At,
            CorrelationId = msg.CorrelationId,
            ProviderMessageId = item.Channel == "web" ? null : item.ExternalId,
        };
        db.Events.Add(evt);

        try
        {
            await db.SaveChangesAsync(ct);
            evt.ConversationId = conversation.Id;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            // The failed insert stays tracked otherwise and would be retried with the next item of the batch.
            db.ChangeTracker.Clear();
            log.LogInformation("Duplicate event ignored (source {Source}, externalId {ExternalId})", source.Name, item.ExternalId);
            return IncomingOutcome.Duplicate;
        }

        var deliveries = await dispatcher.CreateDeliveriesAsync(evt, ct);
        foreach (var delivery in deliveries)
        {
            await queues.EnqueueDeliveryAsync(
                new DeliveryMessage(delivery.Id, evt.WorkspaceId, evt.CorrelationId, msg.TraceParent, AfterAttempt: 0, DeliveryReasons.Initial),
                visibilityDelay: null, ct);
        }
        await notifier.EventCreatedAsync(evt, ct);

        log.LogInformation("Event {EventId} stored from {Source} ({Channel}); {Deliveries} deliveries queued", evt.Id, source.Name, evt.Channel, deliveries.Count);
        db.ChangeTracker.Clear();
        return IncomingOutcome.Stored;
    }
}
