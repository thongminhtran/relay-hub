using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;

namespace RelayHub.Infrastructure.Processing;

public enum IncomingOutcome
{
    /// <summary>Event stored, deliveries created and queued.</summary>
    Stored,
    /// <summary>Same source + ExternalId already stored (webhook retry or queue redelivery). Nothing changed.</summary>
    Duplicate,
    /// <summary>Unknown / disabled source, source name mismatch or unusable payload. Logged and discarded.</summary>
    Dropped,
}

/// <summary>
/// Worker side of ingest: turns an <see cref="IncomingMessage"/> into a Conversation + Event, creates one Pending Delivery per
/// route and queues each of them. Safe to run more than once for the same message (unique index on SourceId + ExternalId).
/// </summary>
public sealed class IncomingProcessor(
    RelayHubDbContext db,
    AmbientWorkspace ambient,
    DeliveryDispatcher dispatcher,
    IRelayQueues queues,
    IRealtimeNotifier realtime,
    ILogger<IncomingProcessor> log)
{
    private const int MaxTextLength = 4000;

    public async Task<IncomingOutcome> ProcessAsync(IncomingMessage msg, CancellationToken ct)
    {
        // The key was verified by the API (HMAC); here we enforce what needs the database: the source exists, is enabled,
        // and the URL's source key names it. Revocation therefore takes effect at processing time, not at ingest.
        ambient.WorkspaceId = null;
        var source = await db.Sources.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.KeyId == msg.KeyId, ct);
        if (source is null || !source.IsEnabled || !string.Equals(source.Name, msg.SourceKey, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Incoming message dropped: key {KeyId} does not belong to an enabled source named {SourceKey}", msg.KeyId, msg.SourceKey);
            return IncomingOutcome.Dropped;
        }
        ambient.WorkspaceId = source.WorkspaceId;

        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(msg.PayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                log.LogWarning("Incoming message dropped: payload is not a JSON object (source {Source})", source.Name);
                return IncomingOutcome.Dropped;
            }
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            log.LogWarning("Incoming message dropped: payload is not valid JSON (source {Source})", source.Name);
            return IncomingOutcome.Dropped;
        }

        var name = GetString(payload, "name") ?? "Unknown";
        var email = GetString(payload, "email");
        var text = GetString(payload, "message") ?? GetString(payload, "text") ?? "";

        var threadKey = (email ?? name).Trim().ToLowerInvariant();
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(c => c.SourceId == source.Id && c.ExternalThreadId == threadKey, ct);
        if (conversation is null)
        {
            conversation = new Conversation { WorkspaceId = source.WorkspaceId, SourceId = source.Id, ExternalThreadId = threadKey, SenderName = name, SenderHandle = email };
            db.Conversations.Add(conversation);
        }
        conversation.LastInboundAt = msg.ReceivedAt;

        var evt = new Event
        {
            WorkspaceId = source.WorkspaceId,
            SourceId = source.Id,
            ExternalId = msg.ExternalId,
            Channel = source.Type == SourceType.Generic ? "web" : source.Type.ToString().ToLowerInvariant(),
            SenderName = name,
            SenderHandle = email,
            Text = text.Length > MaxTextLength ? text[..MaxTextLength] : text,
            PayloadJson = payload.GetRawText(),
            ReceivedAt = msg.ReceivedAt,
            CorrelationId = msg.CorrelationId,
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
            log.LogInformation("Duplicate event ignored (source {Source}, externalId {ExternalId})", source.Name, msg.ExternalId);
            return IncomingOutcome.Duplicate;
        }

        var deliveries = await dispatcher.CreateDeliveriesAsync(evt, ct);
        foreach (var delivery in deliveries)
        {
            await queues.EnqueueDeliveryAsync(
                new DeliveryMessage(delivery.Id, evt.WorkspaceId, evt.CorrelationId, msg.TraceParent, AfterAttempt: 0, DeliveryReasons.Initial),
                visibilityDelay: null, ct);
        }

        log.LogInformation("Event {EventId} stored from {Source} ({Channel}); {Deliveries} deliveries queued", evt.Id, source.Name, evt.Channel, deliveries.Count);
        await realtime.EventCreatedAsync(evt, source.Name, ct);
        return IncomingOutcome.Stored;
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
