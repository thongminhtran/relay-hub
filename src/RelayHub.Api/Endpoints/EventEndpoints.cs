using Microsoft.EntityFrameworkCore;
using RelayHub.Api.Auth;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure.Data;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/events").WithTags("Inbox").RequireAuthorization(AuthPolicies.Member);

        group.MapGet("/", ListAsync).WithName("ListEvents")
             .WithSummary("List / search events in the current workspace. Filters: channel, q, status (pending|delivered|retrying|deadlettered|none), direction (in|out), from/to (ISO 8601).");
        group.MapGet("/{id:long}", GetAsync).WithName("GetEvent").WithSummary("Event detail with deliveries and attempts.");

        return app;
    }

    private static async Task<IResult> ListAsync(RelayHubDbContext db, string? channel, string? q, string? status, string? direction,
        DateTimeOffset? from, DateTimeOffset? to, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Events.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(channel)) query = query.Where(e => e.Channel == channel);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(e => e.Text.Contains(q) || (e.SenderName != null && e.SenderName.Contains(q)));
        if (from is { } f) query = query.Where(e => e.ReceivedAt >= f);
        if (to is { } t) query = query.Where(e => e.ReceivedAt <= t);

        if (!string.IsNullOrWhiteSpace(direction))
        {
            if (!TryParseEnum<MessageDirection>(direction, out var dir)) return ValidationProblem("direction", "Use in or out");
            query = query.Where(e => e.Direction == dir);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (string.Equals(status, "none", StringComparison.OrdinalIgnoreCase))
                query = query.Where(e => !e.Deliveries.Any());
            else if (TryParseEnum<DeliveryStatus>(status, out var st))
                query = query.Where(e => e.Deliveries.Any(d => d.Status == st));
            else
                return ValidationProblem("status", "Use pending, delivered, retrying, deadlettered or none");
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.ReceivedAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new
            {
                e.Id, e.Channel, e.Direction, e.SenderName, e.SenderHandle, e.Text, e.ReceivedAt, e.CorrelationId, e.ConversationId,
                SourceName = db.Sources.Where(s => s.Id == e.SourceId).Select(s => s.Name).FirstOrDefault(),
                Deliveries = e.Deliveries.Select(d => new
                {
                    d.Id, d.DestinationId,
                    DestinationName = db.Destinations.Where(x => x.Id == d.DestinationId).Select(x => x.Name).FirstOrDefault(),
                    d.Status, d.AttemptCount, d.LastError,
                }).ToList(),
            })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = items.Select(e => new
            {
                e.Id, e.Channel, direction = e.Direction.ToString().ToLowerInvariant(), e.SenderName, e.SenderHandle, e.Text, e.ReceivedAt, e.CorrelationId, e.ConversationId, e.SourceName,
                deliveries = e.Deliveries.Select(d => new { d.Id, d.DestinationId, d.DestinationName, status = d.Status.ToString(), d.AttemptCount, d.LastError }),
            }),
            page, pageSize, total,
        });
    }

    private static async Task<IResult> GetAsync(long id, RelayHubDbContext db, CancellationToken ct)
    {
        var evt = await db.Events.AsNoTracking()
            .Include(e => e.Deliveries).ThenInclude(d => d.Attempts)
            .SingleOrDefaultAsync(e => e.Id == id, ct);
        if (evt is null) return NotFound("Event");

        var sourceName = await db.Sources.AsNoTracking().Where(s => s.Id == evt.SourceId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        var destinationIds = evt.Deliveries.Select(d => d.DestinationId).Distinct().ToList();
        var destinationNames = destinationIds.Count == 0
            ? []
            : await db.Destinations.AsNoTracking().Where(d => destinationIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return Results.Ok(new
        {
            evt.Id, evt.Channel, direction = evt.Direction.ToString().ToLowerInvariant(), evt.ConversationId, sourceName,
            from = new { name = evt.SenderName, handle = evt.SenderHandle },
            evt.Text, evt.ReceivedAt, evt.CorrelationId, evt.ProviderMessageId,
            payload = System.Text.Json.JsonDocument.Parse(evt.PayloadJson).RootElement,
            deliveries = evt.Deliveries.Select(d => new
            {
                d.Id, d.DestinationId, destinationName = destinationNames.GetValueOrDefault(d.DestinationId),
                status = d.Status.ToString(), d.AttemptCount, d.NextAttemptAt, d.LastError,
                attempts = d.Attempts.OrderBy(a => a.AttemptNo).Select(a => new { a.AttemptNo, a.StartedAt, a.DurationMs, a.HttpStatus, a.Error }),
            }),
        });
    }
}
