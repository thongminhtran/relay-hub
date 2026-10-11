using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Templates;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Infrastructure.Dispatch;

/// <summary>
/// Creates deliveries for an event and performs delivery attempts. Shared by the API (first attempt, Week 1)
/// and the worker (retries from the queue, Week 2) so the backoff / dead-letter rules live in one place.
/// </summary>
public sealed class DeliveryDispatcher(
    RelayHubDbContext db,
    IEnumerable<IDestinationConnector> connectors,
    ILogger<DeliveryDispatcher> log)
{
    public const int MaxAttempts = 5;

    /// <summary>30 s, 2 min, 10 min, 30 min, 1 h — the wait after attempt 1..4. (Demo workspaces use 2/4/8 s; Week 5.)</summary>
    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
    ];

    /// <summary>One Delivery per enabled route of the event's source, each attempted once. Returns the number of deliveries created.</summary>
    public async Task<int> DispatchAsync(Event evt, Source source, CancellationToken ct)
    {
        var routes = await db.Routes.Where(r => r.SourceId == evt.SourceId && r.IsEnabled).ToListAsync(ct);
        if (routes.Count == 0) return 0;

        var destinationIds = routes.Select(r => r.DestinationId).ToList();
        var destinations = await db.Destinations
            .Where(d => destinationIds.Contains(d.Id) && d.IsEnabled)
            .ToDictionaryAsync(d => d.Id, ct);

        var values = TemplateValues.For(evt, source);
        var created = 0;
        foreach (var route in routes)
        {
            if (!destinations.TryGetValue(route.DestinationId, out var destination)) continue;

            var delivery = new Delivery { EventId = evt.Id, DestinationId = destination.Id };
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync(ct);
            created++;

            await AttemptAsync(delivery, destination, TemplateRenderer.Render(route.Template, values), evt, ct);
        }
        return created;
    }

    /// <summary>Performs one attempt, records it, and moves the delivery to Delivered / Retrying (with backoff) / DeadLettered.</summary>
    public async Task<DeliveryResult> AttemptAsync(Delivery delivery, Destination destination, string text, Event evt, CancellationToken ct)
    {
        var result = await SendAsync(destination, text, evt, ct);

        delivery.AttemptCount++;
        delivery.Attempts.Add(new DeliveryAttempt
        {
            AttemptNo = delivery.AttemptCount,
            DurationMs = result.DurationMs,
            HttpStatus = result.Result.HttpStatus,
            Error = result.Result.Error,
        });

        if (result.Result.Succeeded)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.NextAttemptAt = null;
            delivery.LastError = null;
        }
        else
        {
            delivery.LastError = result.Result.Error;
            if (result.Result.Retryable && delivery.AttemptCount < MaxAttempts)
            {
                delivery.Status = DeliveryStatus.Retrying;
                delivery.NextAttemptAt = DateTimeOffset.UtcNow + Backoff[delivery.AttemptCount - 1];
            }
            else
            {
                delivery.Status = DeliveryStatus.DeadLettered;
                delivery.NextAttemptAt = null;
            }
            log.LogWarning("Delivery {DeliveryId} → {Destination} attempt {Attempt} failed ({Status}): {Error}",
                delivery.Id, destination.Name, delivery.AttemptCount, delivery.Status, result.Result.Error);
        }

        await db.SaveChangesAsync(ct);
        return result.Result;
    }

    /// <summary>"Send test message" for a destination. Nothing is stored.</summary>
    public async Task<DeliveryResult> SendTestAsync(Destination destination, CancellationToken ct)
    {
        var evt = new Event
        {
            WorkspaceId = destination.WorkspaceId, SourceId = 0, ExternalId = "test", Channel = "test",
            SenderName = "RelayHub", Text = "Test message", CorrelationId = Guid.NewGuid().ToString("N"),
        };
        var text = $"✅ RelayHub test message to \"{destination.Name}\" at {DateTimeOffset.UtcNow:u}";
        return (await SendAsync(destination, text, evt, ct)).Result;
    }

    private async Task<(DeliveryResult Result, int DurationMs)> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        var connector = connectors.FirstOrDefault(c => c.Type == destination.Type);
        var sw = Stopwatch.StartNew();
        DeliveryResult result;
        try
        {
            result = connector is null
                ? new DeliveryResult(false, null, $"No connector registered for {destination.Type}", Retryable: false)
                : await connector.SendAsync(destination, text, evt, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            result = new DeliveryResult(false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
        return (result, (int)sw.ElapsedMilliseconds);
    }
}
