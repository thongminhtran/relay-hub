using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Connectors;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Infrastructure.Dispatch;

/// <summary>
/// Owns the delivery rules: which deliveries an event produces, how one attempt is recorded, and the backoff / dead-letter
/// schedule. Used by the worker (attempts from the queue) and by the API (test messages). Sending inline from the API
/// was a Week 1 stop-gap; since Week 2 every attempt comes off the <c>deliveries</c> queue.
/// With <c>mock: true</c> (demo workspaces) every send goes to <see cref="DemoSinkConnector"/> instead of a real connector.
/// </summary>
public sealed class DeliveryDispatcher(
    RelayHubDbContext db,
    IEnumerable<IDestinationConnector> connectors,
    DemoSinkConnector demoSink,
    ILogger<DeliveryDispatcher> log)
{
    public const int MaxAttempts = 5;
    public const string DemoRetryProfile = "demo";

    /// <summary>30 s, 2 min, 10 min, 30 min, 1 h — the wait after attempt 1..5 (the 5th entry is only used by replays).</summary>
    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
    ];

    /// <summary>The public demo workspace retries fast enough to watch: 2, 4, 8, 16, 32 s.</summary>
    public static readonly TimeSpan[] DemoBackoff =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32),
    ];

    /// <summary>How long to wait after the given (1-based) failed attempt, for a workspace retry profile.</summary>
    public static TimeSpan DelayAfter(int attemptCount, string? retryProfile)
    {
        var table = string.Equals(retryProfile, DemoRetryProfile, StringComparison.OrdinalIgnoreCase) ? DemoBackoff : Backoff;
        return table[Math.Clamp(attemptCount, 1, table.Length) - 1];
    }

    /// <summary>
    /// One Pending delivery per enabled route of the event's source whose destination is enabled. Nothing is sent here:
    /// the caller enqueues one <c>DeliveryMessage</c> per delivery and the worker attempts them.
    /// </summary>
    public async Task<List<Delivery>> CreateDeliveriesAsync(Event evt, CancellationToken ct)
    {
        var routes = await db.Routes.Where(r => r.SourceId == evt.SourceId && r.IsEnabled).ToListAsync(ct);
        if (routes.Count == 0) return [];

        var destinationIds = routes.Select(r => r.DestinationId).ToList();
        var enabledDestinations = (await db.Destinations
            .Where(d => destinationIds.Contains(d.Id) && d.IsEnabled)
            .Select(d => d.Id)
            .ToListAsync(ct)).ToHashSet();

        var created = new List<Delivery>();
        foreach (var route in routes)
        {
            if (!enabledDestinations.Contains(route.DestinationId)) continue;
            var delivery = new Delivery { EventId = evt.Id, DestinationId = route.DestinationId, RouteId = route.Id };
            db.Deliveries.Add(delivery);
            created.Add(delivery);
        }
        if (created.Count > 0) await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>Performs one attempt, records it, and moves the delivery to Delivered / Retrying (with backoff) / DeadLettered.</summary>
    /// <param name="mock">True for demo workspaces: send through <see cref="DemoSinkConnector"/> whatever the destination type.</param>
    public async Task<DeliveryResult> AttemptAsync(Delivery delivery, Destination destination, string text, Event evt, string? retryProfile, bool mock = false, CancellationToken ct = default)
    {
        var result = await SendAsync(destination, text, evt, mock, ct);

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
                delivery.NextAttemptAt = DateTimeOffset.UtcNow + DelayAfter(delivery.AttemptCount, retryProfile);
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
    /// <param name="mock">True for demo workspaces: the test goes to <see cref="DemoSinkConnector"/>, never to the real channel.</param>
    public async Task<DeliveryResult> SendTestAsync(Destination destination, bool mock = false, CancellationToken ct = default)
    {
        var evt = new Event
        {
            WorkspaceId = destination.WorkspaceId, SourceId = 0, ExternalId = "test", Channel = "test",
            SenderName = "RelayHub", Text = "Test message", CorrelationId = Guid.NewGuid().ToString("N"),
        };
        var text = $"✅ RelayHub test message to \"{destination.Name}\" at {DateTimeOffset.UtcNow:u}";
        return (await SendAsync(destination, text, evt, mock, ct)).Result;
    }

    private async Task<(DeliveryResult Result, int DurationMs)> SendAsync(Destination destination, string text, Event evt, bool mock, CancellationToken ct)
    {
        var connector = mock ? null : connectors.FirstOrDefault(c => c.Type == destination.Type);
        var sw = Stopwatch.StartNew();
        DeliveryResult result;
        try
        {
            result = mock
                ? await demoSink.SendAsync(destination, text, evt, ct)
                : connector is null
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
