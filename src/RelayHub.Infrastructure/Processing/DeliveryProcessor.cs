using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Core.Templates;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;

namespace RelayHub.Infrastructure.Processing;

public enum DeliveryOutcome
{
    Delivered,
    /// <summary>Attempt failed with a retryable error; re-enqueued with the backoff delay.</summary>
    Retrying,
    /// <summary>Gave up (5 attempts, non-retryable error or missing configuration). The operator was alerted.</summary>
    DeadLettered,
    /// <summary>Duplicate or stale message: the delivery already succeeded or moved past this attempt.</summary>
    Skipped,
}

/// <summary>
/// Worker side of delivery: one queue message = one attempt. The template is re-rendered on every attempt so a fixed
/// route helps a replay. Connector failures are handled here (backoff by re-enqueue, dead-letter + alert); only crashes
/// (SQL down, bug) throw, which lets the Functions host retry and finally move the message to the poison queue.
/// </summary>
public sealed class DeliveryProcessor(
    RelayHubDbContext db,
    AmbientWorkspace ambient,
    DeliveryDispatcher dispatcher,
    IRelayQueues queues,
    IOpsAlerts alerts,
    ILogger<DeliveryProcessor> log)
{
    public async Task<DeliveryOutcome> ProcessAsync(DeliveryMessage msg, CancellationToken ct)
    {
        ambient.WorkspaceId = msg.WorkspaceId;

        var delivery = await db.Deliveries.Include(d => d.Attempts).SingleOrDefaultAsync(d => d.Id == msg.DeliveryId, ct);
        if (delivery is null)
        {
            log.LogWarning("Delivery {DeliveryId} not found; message dropped", msg.DeliveryId);
            return DeliveryOutcome.Skipped;
        }
        if (delivery.Status == DeliveryStatus.Delivered || delivery.AttemptCount != msg.AfterAttempt)
        {
            // At-least-once delivery: a duplicate, or a scheduled retry that a replay already overtook.
            log.LogInformation("Delivery {DeliveryId} skipped ({Reason} message after attempt {Expected}; delivery is {Status} after {Attempts})",
                delivery.Id, msg.Reason, msg.AfterAttempt, delivery.Status, delivery.AttemptCount);
            return DeliveryOutcome.Skipped;
        }

        var evt = await db.Events.SingleOrDefaultAsync(e => e.Id == delivery.EventId, ct);
        var source = evt is null ? null : await db.Sources.SingleOrDefaultAsync(s => s.Id == evt.SourceId, ct);
        var destination = await db.Destinations.SingleOrDefaultAsync(d => d.Id == delivery.DestinationId, ct);
        if (evt is null || source is null || destination is null)
            return await GiveUpAsync(delivery, "The event, its source or the destination no longer exists", ct);
        if (!destination.IsEnabled)
            return await GiveUpAsync(delivery, $"Destination \"{destination.Name}\" is disabled", ct);

        var route = delivery.RouteId is { } routeId ? await db.Routes.SingleOrDefaultAsync(r => r.Id == routeId, ct) : null;
        var text = TemplateRenderer.Render(route?.Template ?? Route.DefaultTemplate, TemplateValues.For(evt, source));
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == msg.WorkspaceId, ct);
        var retryProfile = workspace?.RetryProfile;

        // Demo workspaces never reach a real channel: the attempt goes to DemoSinkConnector (plan 9.3).
        await dispatcher.AttemptAsync(delivery, destination, text, evt, retryProfile, mock: workspace?.IsDemo ?? false, ct);

        switch (delivery.Status)
        {
            case DeliveryStatus.Retrying:
            {
                var delay = DeliveryDispatcher.DelayAfter(delivery.AttemptCount, retryProfile);
                await queues.EnqueueDeliveryAsync(msg with { AfterAttempt = delivery.AttemptCount, Reason = DeliveryReasons.Retry }, delay, ct);
                log.LogInformation("Delivery {DeliveryId} retry {Next} scheduled in {Delay}", delivery.Id, delivery.AttemptCount + 1, delay);
                return DeliveryOutcome.Retrying;
            }
            case DeliveryStatus.DeadLettered:
                await alerts.NotifyAsync(
                    $"Delivery {delivery.Id} dead-lettered",
                    $"Destination \"{destination.Name}\" ({destination.Type}), event {evt.Id} from {evt.SenderName} via {evt.Channel}, {delivery.AttemptCount} attempt(s). " +
                    $"Last error: {delivery.LastError}. Correlation {evt.CorrelationId}. Replay: POST /api/v1/deliveries/{delivery.Id}/replay", ct);
                return DeliveryOutcome.DeadLettered;
            default:
                log.LogInformation("Delivery {DeliveryId} delivered to {Destination} on attempt {Attempt}", delivery.Id, destination.Name, delivery.AttemptCount);
                return DeliveryOutcome.Delivered;
        }
    }

    private async Task<DeliveryOutcome> GiveUpAsync(Delivery delivery, string reason, CancellationToken ct)
    {
        delivery.Status = DeliveryStatus.DeadLettered;
        delivery.NextAttemptAt = null;
        delivery.LastError = reason;
        await db.SaveChangesAsync(ct);
        log.LogWarning("Delivery {DeliveryId} dead-lettered without an attempt: {Reason}", delivery.Id, reason);
        await alerts.NotifyAsync($"Delivery {delivery.Id} dead-lettered", reason, ct);
        return DeliveryOutcome.DeadLettered;
    }
}
