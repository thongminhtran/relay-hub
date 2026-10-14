using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RelayHub.Api.Auth;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure.Data;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class DeliveryEndpoints
{
    public static IEndpointRouteBuilder MapDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/deliveries").WithTags("Inbox").RequireAuthorization(AuthPolicies.Member);

        group.MapGet("/{id:long}", GetAsync).WithName("GetDelivery").WithSummary("One delivery with its attempts.");
        group.MapPost("/{id:long}/replay", ReplayAsync).WithName("ReplayDelivery")
             .WithSummary("Re-queue a retrying or dead-lettered delivery for one more attempt (the attempt history is kept).")
             .Produces(StatusCodes.Status202Accepted)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetAsync(long id, RelayHubDbContext db, CancellationToken ct)
    {
        var delivery = await ScopedDeliveries(db).AsNoTracking().Include(d => d.Attempts).SingleOrDefaultAsync(d => d.Id == id, ct);
        return delivery is null ? NotFound("Delivery") : Results.Ok(ToDto(delivery));
    }

    private static async Task<IResult> ReplayAsync(long id, RelayHubDbContext db, IRelayQueues queues, IRealtimeNotifier realtime, CancellationToken ct)
    {
        var delivery = await ScopedDeliveries(db).Include(d => d.Attempts).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (delivery is null) return NotFound("Delivery");

        switch (delivery.Status)
        {
            case DeliveryStatus.Delivered:
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Delivery already succeeded");
            case DeliveryStatus.Pending:
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Delivery is already queued");
        }

        var evt = await db.Events.AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);

        // Pending + a fresh message. A retry message still scheduled in the queue becomes stale (AfterAttempt no longer matches once this attempt runs).
        delivery.Status = DeliveryStatus.Pending;
        delivery.NextAttemptAt = null;
        await db.SaveChangesAsync(ct);

        await queues.EnqueueDeliveryAsync(
            new DeliveryMessage(delivery.Id, evt.WorkspaceId, evt.CorrelationId, Activity.Current?.Id, AfterAttempt: delivery.AttemptCount, DeliveryReasons.Replay),
            visibilityDelay: null, ct);
        await realtime.DeliveryUpdatedAsync(delivery, evt.WorkspaceId, ct);

        return Results.Accepted(value: ToDto(delivery));
    }

    /// <summary>Deliveries have no WorkspaceId of their own: scope them through their event (which carries the global query filter).</summary>
    private static IQueryable<Delivery> ScopedDeliveries(RelayHubDbContext db)
        => db.Deliveries.Where(d => db.Events.Any(e => e.Id == d.EventId));

    private static object ToDto(Delivery d) => new
    {
        d.Id, d.EventId, d.DestinationId, d.RouteId, status = d.Status.ToString(), d.AttemptCount, d.NextAttemptAt, d.LastError,
        attempts = d.Attempts.OrderBy(a => a.AttemptNo).Select(a => new { a.AttemptNo, a.StartedAt, a.DurationMs, a.HttpStatus, a.Error }),
    };
}
