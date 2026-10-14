using Microsoft.EntityFrameworkCore;
using RelayHub.Api.Auth;
using RelayHub.Core.Domain;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class StatsEndpoints
{
    public sealed record TodayStats(int Events, int Replies, int Delivered, int Retrying, int DeadLettered);
    public sealed record TotalStats(int Events, int Conversations, int Delivered, int DeadLettered);
    public sealed record ChannelCount(string Channel, int Count);
    public sealed record SeriesPoint(string Date, int Events, int Delivered, int Failed);
    public sealed record StatsSummary(TodayStats Today, TotalStats Total, double? SuccessRate, IReadOnlyList<ChannelCount> ByChannel, IReadOnlyList<SeriesPoint> Series);

    private const int SeriesDays = 7;

    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/stats").WithTags("Inbox").RequireAuthorization(AuthPolicies.Member)
           .MapGet("/summary", SummaryAsync).WithName("StatsSummary").Produces<StatsSummary>()
           .WithSummary("Dashboard numbers: today (UTC), all time, 7-day success rate, inbound by channel and a 7-day series. Three grouped queries, nothing paged through memory.");
        return app;
    }

    private static async Task<IResult> SummaryAsync(RelayHubDbContext db, AmbientWorkspace ambient, CancellationToken ct)
    {
        var workspaceId = ambient.WorkspaceIdOrDefault();
        var today = DateTimeOffset.UtcNow.Date;
        var windowStart = new DateTimeOffset(today.AddDays(-(SeriesDays - 1)), TimeSpan.Zero);

        // 1. Events of the window grouped by UTC day / direction / channel (series, today's events + replies, by-channel).
        var eventGroups = await db.Events.AsNoTracking()
            .Where(e => e.ReceivedAt >= windowStart)
            .GroupBy(e => new { Day = e.ReceivedAt.Date, e.Direction, e.Channel })
            .Select(g => new { g.Key.Day, g.Key.Direction, g.Key.Channel, Count = g.Count() })
            .ToListAsync(ct);

        // 2. Deliveries of the window grouped by the day of their event / status (series delivered + failed, today's delivery counters, success rate).
        var deliveryGroups = await (
                from d in db.Deliveries.AsNoTracking()
                join e in db.Events.AsNoTracking() on d.EventId equals e.Id
                where e.ReceivedAt >= windowStart
                group d by new { Day = e.ReceivedAt.Date, d.Status } into g
                select new { g.Key.Day, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        // 3. All-time totals in one round trip (scalar subqueries; deliveries are scoped through their event's workspace filter).
        var totals = await db.Workspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => new
            {
                Events = db.Events.Count(e => e.Direction == MessageDirection.In),
                Conversations = db.Conversations.Count(),
                Delivered = db.Deliveries.Count(d => d.Status == DeliveryStatus.Delivered && db.Events.Any(e => e.Id == d.EventId)),
                DeadLettered = db.Deliveries.Count(d => d.Status == DeliveryStatus.DeadLettered && db.Events.Any(e => e.Id == d.EventId)),
            })
            .SingleOrDefaultAsync(ct) ?? new { Events = 0, Conversations = 0, Delivered = 0, DeadLettered = 0 };

        var todayStats = new TodayStats(
            Events: eventGroups.Where(g => g.Day == today && g.Direction == MessageDirection.In).Sum(g => g.Count),
            Replies: eventGroups.Where(g => g.Day == today && g.Direction == MessageDirection.Out).Sum(g => g.Count),
            Delivered: deliveryGroups.Where(g => g.Day == today && g.Status == DeliveryStatus.Delivered).Sum(g => g.Count),
            Retrying: deliveryGroups.Where(g => g.Day == today && g.Status == DeliveryStatus.Retrying).Sum(g => g.Count),
            DeadLettered: deliveryGroups.Where(g => g.Day == today && g.Status == DeliveryStatus.DeadLettered).Sum(g => g.Count));

        var windowDelivered = deliveryGroups.Where(g => g.Status == DeliveryStatus.Delivered).Sum(g => g.Count);
        var windowFailed = deliveryGroups.Where(g => g.Status == DeliveryStatus.DeadLettered).Sum(g => g.Count);
        double? successRate = windowDelivered + windowFailed == 0 ? null : (double)windowDelivered / (windowDelivered + windowFailed);

        var byChannel = eventGroups.Where(g => g.Direction == MessageDirection.In)
            .GroupBy(g => g.Channel)
            .Select(g => new ChannelCount(g.Key, g.Sum(x => x.Count)))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Channel)
            .ToList();

        var series = Enumerable.Range(0, SeriesDays)
            .Select(i => today.AddDays(-(SeriesDays - 1) + i))
            .Select(day => new SeriesPoint(
                day.ToString("yyyy-MM-dd"),
                eventGroups.Where(g => g.Day == day && g.Direction == MessageDirection.In).Sum(g => g.Count),
                deliveryGroups.Where(g => g.Day == day && g.Status == DeliveryStatus.Delivered).Sum(g => g.Count),
                deliveryGroups.Where(g => g.Day == day && g.Status == DeliveryStatus.DeadLettered).Sum(g => g.Count)))
            .ToList();

        return Results.Ok(new StatsSummary(todayStats, new TotalStats(totals.Events, totals.Conversations, totals.Delivered, totals.DeadLettered), successRate, byChannel, series));
    }
}
