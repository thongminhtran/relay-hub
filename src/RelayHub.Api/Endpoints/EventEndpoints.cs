using Microsoft.EntityFrameworkCore;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Api.Endpoints;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/events").WithTags("Inbox");

        group.MapGet("/", ListAsync).WithName("ListEvents").WithSummary("List / search events in the current workspace.");
        group.MapGet("/{id:long}", GetAsync).WithName("GetEvent").WithSummary("Event detail with deliveries and attempts.");

        return app;
    }

    private static async Task<IResult> ListAsync(RelayHubDbContext db, string? channel, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Events.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(channel)) query = query.Where(e => e.Channel == channel);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(e => e.Text.Contains(q) || (e.SenderName != null && e.SenderName.Contains(q)));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.ReceivedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new
            {
                e.Id, e.Channel, e.Direction, e.SenderName, e.SenderHandle, e.Text, e.ReceivedAt, e.CorrelationId, e.ConversationId,
                deliveries = e.Deliveries.Select(d => new { d.Id, d.DestinationId, status = d.Status.ToString(), d.AttemptCount, d.LastError }),
            })
            .ToListAsync(ct);

        return Results.Ok(new { items, page, pageSize, total });
    }

    private static async Task<IResult> GetAsync(long id, RelayHubDbContext db, CancellationToken ct)
    {
        var evt = await db.Events.AsNoTracking()
            .Include(e => e.Deliveries).ThenInclude(d => d.Attempts)
            .SingleOrDefaultAsync(e => e.Id == id, ct);
        if (evt is null) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Event not found");

        return Results.Ok(new
        {
            evt.Id, evt.Channel, direction = evt.Direction.ToString().ToLowerInvariant(), evt.ConversationId,
            from = new { name = evt.SenderName, handle = evt.SenderHandle },
            evt.Text, evt.ReceivedAt, evt.CorrelationId,
            payload = System.Text.Json.JsonDocument.Parse(evt.PayloadJson).RootElement,
            deliveries = evt.Deliveries.Select(d => new
            {
                d.Id, d.DestinationId, status = d.Status.ToString(), d.AttemptCount, d.NextAttemptAt, d.LastError,
                attempts = d.Attempts.OrderBy(a => a.AttemptNo).Select(a => new { a.AttemptNo, a.StartedAt, a.DurationMs, a.HttpStatus, a.Error }),
            }),
        });
    }
}
