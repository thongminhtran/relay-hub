using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RelayHub.Core.Demo;
using RelayHub.Core.Domain;
using RelayHub.Core.Security;
using RelayHub.Infrastructure.Connectors;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;
using DomainRoute = RelayHub.Core.Domain.Route;

namespace RelayHub.Api.Startup;

public sealed record DemoSeedSummary(int WorkspaceId, bool SampleDataSeeded, int Conversations, int Events);

/// <summary>
/// The public demo workspace (plan section 9): five sources (one per channel), two mocked destinations, routes, and a week of
/// realistic sample conversations for a small web/design studio. Idempotent: configuration is keyed on names, sample data is
/// only inserted when the workspace has no events. Runs unscoped (<c>IgnoreQueryFilters</c>) because the startup seeder has no
/// ambient workspace; every query still names the demo workspace explicitly.
/// </summary>
public static class DemoSeeder
{
    /// <summary>Ensures workspace, sources, destinations and routes exist, then seeds the sample data once.</summary>
    public static async Task<DemoSeedSummary> RunAsync(RelayHubDbContext db, ILogger log, CancellationToken ct = default)
    {
        var demo = await db.Workspaces.FirstOrDefaultAsync(w => w.IsDemo, ct);
        if (demo is null)
        {
            demo = new Workspace { Name = DemoCatalog.WorkspaceName, IsDemo = true, RetryProfile = DeliveryDispatcher.DemoRetryProfile };
            db.Workspaces.Add(demo);
            await db.SaveChangesAsync(ct);
        }

        var config = await EnsureConfigurationAsync(db, demo.Id, ct);

        if (await db.Events.IgnoreQueryFilters().AnyAsync(e => e.WorkspaceId == demo.Id, ct))
        {
            var conversations = await db.Conversations.IgnoreQueryFilters().CountAsync(c => c.WorkspaceId == demo.Id, ct);
            var events = await db.Events.IgnoreQueryFilters().CountAsync(e => e.WorkspaceId == demo.Id, ct);
            return new DemoSeedSummary(demo.Id, SampleDataSeeded: false, conversations, events);
        }

        var seeded = await SeedSampleDataAsync(db, demo.Id, config, ct);
        log.LogInformation("Demo workspace {WorkspaceId} seeded with {Conversations} conversations and {Events} events", demo.Id, seeded.Conversations, seeded.Events);
        return seeded;
    }

    /// <summary>Removes every event, delivery, attempt and conversation of the workspace (the demo reset).</summary>
    public static async Task ClearAsync(RelayHubDbContext db, int workspaceId, CancellationToken ct)
    {
        var eventIds = db.Events.IgnoreQueryFilters().Where(e => e.WorkspaceId == workspaceId).Select(e => e.Id);
        var deliveryIds = db.Deliveries.Where(d => eventIds.Contains(d.EventId)).Select(d => d.Id);

        await db.DeliveryAttempts.Where(a => deliveryIds.Contains(a.DeliveryId)).ExecuteDeleteAsync(ct);
        await db.Deliveries.Where(d => eventIds.Contains(d.EventId)).ExecuteDeleteAsync(ct);
        await db.Events.IgnoreQueryFilters().Where(e => e.WorkspaceId == workspaceId).ExecuteDeleteAsync(ct);
        await db.Conversations.IgnoreQueryFilters().Where(c => c.WorkspaceId == workspaceId).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Plan 9.3 cap: when the workspace holds <see cref="DemoCatalog.MaxEvents"/> events or more, the oldest
    /// <see cref="DemoCatalog.TrimBatch"/> (with their deliveries and attempts) are removed. Conversations stay.
    /// Returns how many events were removed.
    /// </summary>
    public static async Task<int> TrimOldestAsync(RelayHubDbContext db, int workspaceId, CancellationToken ct)
    {
        var total = await db.Events.IgnoreQueryFilters().CountAsync(e => e.WorkspaceId == workspaceId, ct);
        if (total < DemoCatalog.MaxEvents) return 0;

        var oldest = await db.Events.IgnoreQueryFilters()
            .Where(e => e.WorkspaceId == workspaceId)
            .OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id)
            .Take(DemoCatalog.TrimBatch)
            .Select(e => e.Id)
            .ToListAsync(ct);
        if (oldest.Count == 0) return 0;

        var deliveryIds = db.Deliveries.Where(d => oldest.Contains(d.EventId)).Select(d => d.Id);
        await db.DeliveryAttempts.Where(a => deliveryIds.Contains(a.DeliveryId)).ExecuteDeleteAsync(ct);
        await db.Deliveries.Where(d => oldest.Contains(d.EventId)).ExecuteDeleteAsync(ct);
        return await db.Events.IgnoreQueryFilters().Where(e => oldest.Contains(e.Id)).ExecuteDeleteAsync(ct);
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Configuration: sources, destinations, routes
    // ---------------------------------------------------------------------------------------------------------------------

    private sealed record DemoConfig(Dictionary<string, Source> SourcesByChannel, Destination Discord, Destination Flaky);

    private static async Task<DemoConfig> EnsureConfigurationAsync(RelayHubDbContext db, int workspaceId, CancellationToken ct)
    {
        var sources = await db.Sources.IgnoreQueryFilters().Where(s => s.WorkspaceId == workspaceId).ToListAsync(ct);

        Source Ensure(string name, SourceType type, string? externalAccountId = null, string configJson = "{}")
        {
            var existing = sources.FirstOrDefault(s => s.Name == name);
            if (existing is not null) return existing;
            var source = new Source
            {
                WorkspaceId = workspaceId, Type = type, Name = name, KeyId = ApiKeys.NewKeyId(),
                ExternalAccountId = externalAccountId, ConfigJson = configJson,
            };
            db.Sources.Add(source);
            sources.Add(source);
            return source;
        }

        var byChannel = new Dictionary<string, Source>
        {
            [DemoChannels.Web] = Ensure(DemoCatalog.WebsiteSource, SourceType.Generic),
            [DemoChannels.Instagram] = Ensure(DemoCatalog.InstagramSource, SourceType.Instagram, DemoCatalog.InstagramAccountId),
            [DemoChannels.Facebook] = Ensure(DemoCatalog.FacebookSource, SourceType.Facebook, DemoCatalog.FacebookPageId),
            [DemoChannels.Telegram] = Ensure(DemoCatalog.TelegramSource, SourceType.Telegram, configJson: DemoCatalog.TelegramConfigJson),
            [DemoChannels.Email] = Ensure(DemoCatalog.EmailSource, SourceType.Email, DemoCatalog.EmailAddress),
        };

        var destinations = await db.Destinations.IgnoreQueryFilters().Where(d => d.WorkspaceId == workspaceId).ToListAsync(ct);

        Destination EnsureDestination(string name, string configJson)
        {
            var existing = destinations.FirstOrDefault(d => d.Name == name);
            if (existing is not null) return existing;
            var destination = new Destination { WorkspaceId = workspaceId, Type = DestinationType.Discord, Name = name, ConfigJson = configJson };
            db.Destinations.Add(destination);
            destinations.Add(destination);
            return destination;
        }

        var discord = EnsureDestination(DemoCatalog.DiscordDestination, DemoCatalog.DiscordDestinationConfigJson);
        var flaky = EnsureDestination(DemoCatalog.FlakyDestination, DemoCatalog.FlakyDestinationConfigJson);
        await db.SaveChangesAsync(ct);

        var routes = await db.Routes.IgnoreQueryFilters().Where(r => r.WorkspaceId == workspaceId).ToListAsync(ct);
        void EnsureRoute(Source source, Destination destination)
        {
            if (routes.Any(r => r.SourceId == source.Id && r.DestinationId == destination.Id)) return;
            db.Routes.Add(new DomainRoute { WorkspaceId = workspaceId, SourceId = source.Id, DestinationId = destination.Id });
        }

        foreach (var source in byChannel.Values) EnsureRoute(source, discord);
        EnsureRoute(byChannel[DemoChannels.Web], flaky);
        await db.SaveChangesAsync(ct);

        return new DemoConfig(byChannel, discord, flaky);
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // Sample data: a week in the life of a small web/design studio
    // ---------------------------------------------------------------------------------------------------------------------

    private enum FlakyOutcome { Delivered, Retrying, DeadLettered }

    /// <param name="HoursAgo">When the message happened, relative to seed time.</param>
    /// <param name="Flaky">Only meaningful for inbound web messages, which also route to the flaky destination.</param>
    private sealed record Msg(double HoursAgo, MessageDirection Direction, string Text, FlakyOutcome Flaky = FlakyOutcome.Delivered);

    private sealed record SampleThread(string Channel, DemoCustomer Customer, params Msg[] Messages);

    private static Msg In(double hoursAgo, string text, FlakyOutcome flaky = FlakyOutcome.Delivered) => new(hoursAgo, MessageDirection.In, text, flaky);
    private static Msg Out(double hoursAgo, string text) => new(hoursAgo, MessageDirection.Out, text);

    private static IReadOnlyList<SampleThread> SampleThreads()
    {
        var anna = DemoCustomers.Pool[0];
        var marco = DemoCustomers.Pool[1];
        var linh = DemoCustomers.Pool[2];
        var jamal = DemoCustomers.Pool[3];
        var sofia = DemoCustomers.Pool[4];
        var minh = DemoCustomers.FromName("Minh Le");
        var chloe = DemoCustomers.FromName("Chloe Martin");
        var daniel = DemoCustomers.FromName("Daniel Kim");
        var hiro = DemoCustomers.FromName("Hiro Tanaka");
        var priya = DemoCustomers.FromName("Priya Patel");
        var olivia = DemoCustomers.FromName("Olivia Brown");
        var tom = DemoCustomers.FromName("Tom Becker");

        return
        [
            // Instagram: Anna's last message is 2 h old → the 24 h reply window is open.
            new(DemoChannels.Instagram, anna,
                In(52, "Hi! Love your work. How much for a simple website for my bakery? Nothing fancy, menu + opening hours + a few photos."),
                Out(51.5, "Hi Anna! Thanks for reaching out. A 5-page site with a menu starts at $1,200. Want to hop on a quick call this week?"),
                In(50, "That works. Can we do Thursday afternoon?"),
                In(49.5, "Thursday 3 pm?"),
                In(2, "Hi again! Sorry to chase, did you get my feedback on the mock-up? The logo looks great by the way.")),
            // Instagram: Minh's last message is 30 h old → window closed.
            new(DemoChannels.Instagram, minh,
                In(31, "Could you send the logo in a warmer colour? The orange feels a bit too bright on the photos."),
                Out(30.5, "Sure, Minh. I'll send two warmer variants tonight."),
                In(30, "Perfect, thank you!")),
            new(DemoChannels.Instagram, chloe,
                In(120, "Do you also design Instagram post templates?"),
                In(118, "Around 10 templates for my yoga studio, same style as the website.")),
            new(DemoChannels.Facebook, marco,
                In(150, "Hello, we run a small restaurant. Do you build websites with an online menu?"),
                Out(149, "Ciao Marco! Yes: menu, photos and reservations. I'll email a quote tomorrow."),
                In(100, "Got the quote, looks good. Can we add online ordering later?"),
                In(99, "Also, do you take a deposit?"),
                In(26, "Deposit sent. When do we start?")),
            new(DemoChannels.Facebook, daniel,
                In(25, "Is the studio open this Saturday? I'd like to drop by with my laptop."),
                In(24.5, "Around 11 am would be ideal.")),
            new(DemoChannels.Telegram, linh,
                In(72, "Hi, quick question about invoice #1042. Is the deposit included?"),
                In(71, "Also the due date says the 30th, is that right?"),
                In(70, "Thanks!"),
                In(20, "Paid today, please confirm when it lands.")),
            new(DemoChannels.Telegram, hiro,
                In(140, "Our booking page is broken on mobile, can you take a look?"),
                In(139, "Screenshot attached: the calendar overflows the screen on iPhone.")),
            new(DemoChannels.Email, jamal,
                In(96, "Quote for a gym landing page\n\nHi, we're launching a 6-week bootcamp in January and need a landing page with a sign-up form. Could you send a quote and a rough timeline?"),
                In(90, "Re: Quote for a gym landing page\n\nSounds good. Can you include a class schedule section as well?"),
                In(48, "Re: Quote for a gym landing page\n\nApproved. Please send the invoice and we'll pay the deposit this week.")),
            new(DemoChannels.Email, priya,
                In(44, "Invoice #1041 payment confirmation\n\nHi, the transfer went out this morning. Could you confirm once it lands?"),
                In(10, "Re: Invoice #1041 payment confirmation\n\nDid it arrive? Our bank still shows it as pending.")),
            new(DemoChannels.Web, sofia,
                In(28, "We're a dental clinic with two locations. Could you quote a website with online booking?"),
                In(27, "Forgot to mention: we also need the site in Spanish."),
                In(6, "Any update on the quote?")),
            // Web → flaky destination gave up after 5 attempts (what the "Simulate failure" switch produces).
            new(DemoChannels.Web, olivia,
                In(125, "What's your hourly rate for a Shopify theme tweak? Mostly colours and fonts.", FlakyOutcome.DeadLettered)),
            // Web → flaky destination still retrying (3 attempts so far, next one scheduled).
            new(DemoChannels.Web, tom,
                In(0.25, "Can you send the final logo files as SVG?", FlakyOutcome.Retrying)),
        ];
    }

    private static async Task<DemoSeedSummary> SeedSampleDataAsync(RelayHubDbContext db, int workspaceId, DemoConfig config, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var threads = SampleThreads();

        // Conversations first (events reference them by id). Idempotent on (SourceId, ExternalThreadId) in case an earlier seed died half-way.
        var conversations = new Dictionary<SampleThread, Conversation>();
        foreach (var thread in threads)
        {
            var source = config.SourcesByChannel[thread.Channel];
            var threadId = ThreadId(thread.Channel, thread.Customer);
            var conversation = await db.Conversations.IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.SourceId == source.Id && c.ExternalThreadId == threadId, ct);
            if (conversation is null)
            {
                conversation = new Conversation
                {
                    WorkspaceId = workspaceId, SourceId = source.Id, ExternalThreadId = threadId,
                    SenderName = thread.Customer.Name, SenderHandle = Handle(thread.Channel, thread.Customer),
                };
                db.Conversations.Add(conversation);
            }
            conversation.LastInboundAt = thread.Messages.Where(m => m.Direction == MessageDirection.In).Select(m => At(now, m)).Max();
            conversation.LastOutboundAt = thread.Messages.Where(m => m.Direction == MessageDirection.Out).Select(m => (DateTimeOffset?)At(now, m)).Max();
            conversations[thread] = conversation;
        }
        await db.SaveChangesAsync(ct);

        // Events in chronological order (ids then follow time, which the inbox and the cap both like).
        var timeline = threads
            .SelectMany(t => t.Messages.Select(m => (Thread: t, Msg: m)))
            .OrderByDescending(x => x.Msg.HoursAgo)
            .ToList();

        var eventCount = 0;
        foreach (var (thread, msg) in timeline)
        {
            var source = config.SourcesByChannel[thread.Channel];
            var conversation = conversations[thread];
            var at = At(now, msg);
            Event evt;

            if (msg.Direction == MessageDirection.Out)
            {
                evt = new Event
                {
                    WorkspaceId = workspaceId, SourceId = source.Id, ConversationId = conversation.Id, Direction = MessageDirection.Out,
                    ExternalId = $"out:{Guid.NewGuid()}", Channel = thread.Channel, SenderName = "You", SenderHandle = null,
                    Text = msg.Text, PayloadJson = JsonSerializer.Serialize(new { text = msg.Text }), ReceivedAt = at,
                    CorrelationId = Guid.NewGuid().ToString("N"), ProviderMessageId = $"demo-{Guid.NewGuid().ToString("N")[..8]}",
                };
            }
            else
            {
                var simulated = DemoPayloads.Build(thread.Channel, thread.Customer, msg.Text, config.SourcesByChannel[DemoChannels.Web].KeyId, at);
                evt = new Event
                {
                    WorkspaceId = workspaceId, SourceId = source.Id, ConversationId = conversation.Id, Direction = MessageDirection.In,
                    ExternalId = simulated.ExternalId, Channel = thread.Channel, SenderName = thread.Customer.Name,
                    SenderHandle = Handle(thread.Channel, thread.Customer), Text = StoredText(thread.Channel, msg.Text),
                    PayloadJson = simulated.PayloadJson, ReceivedAt = at, CorrelationId = Guid.NewGuid().ToString("N"),
                };
                evt.Deliveries.Add(Delivered(config.Discord, at));
                if (thread.Channel == DemoChannels.Web)
                    evt.Deliveries.Add(msg.Flaky switch
                    {
                        FlakyOutcome.Retrying => Failing(config.Flaky, at, attempts: 3, deadLettered: false, now),
                        FlakyOutcome.DeadLettered => Failing(config.Flaky, at, attempts: DeliveryDispatcher.MaxAttempts, deadLettered: true, now),
                        _ => Delivered(config.Flaky, at),
                    });
            }

            db.Events.Add(evt);
            eventCount++;
        }
        await db.SaveChangesAsync(ct);

        return new DemoSeedSummary(workspaceId, SampleDataSeeded: true, conversations.Count, eventCount);
    }

    private static DateTimeOffset At(DateTimeOffset now, Msg msg) => now - TimeSpan.FromHours(msg.HoursAgo);

    /// <summary>Conversation key the worker's parsers use per channel (see the shared contract).</summary>
    private static string ThreadId(string channel, DemoCustomer customer) => channel switch
    {
        DemoChannels.Instagram or DemoChannels.Facebook => customer.MetaId,
        DemoChannels.Telegram => customer.TelegramId.ToString(),
        _ => customer.Email.ToLowerInvariant(),
    };

    private static string? Handle(string channel, DemoCustomer customer) => channel switch
    {
        DemoChannels.Instagram or DemoChannels.Telegram => "@" + customer.Handle,
        DemoChannels.Facebook => null,
        _ => customer.Email,
    };

    /// <summary>Emails are stored as subject + blank line + body, like the worker does.</summary>
    private static string StoredText(string channel, string text)
    {
        if (channel != DemoChannels.Email) return text;
        var (subject, body) = DemoPayloads.SplitEmail(text);
        return $"{subject}\n\n{body}";
    }

    private static Delivery Delivered(Destination destination, DateTimeOffset receivedAt)
    {
        var delivery = new Delivery { DestinationId = destination.Id, Status = DeliveryStatus.Delivered, AttemptCount = 1 };
        delivery.Attempts.Add(new DeliveryAttempt
        {
            AttemptNo = 1, StartedAt = receivedAt.AddSeconds(Random.Shared.Next(1, 4)),
            DurationMs = Random.Shared.Next(60, 181), HttpStatus = 200,
        });
        return delivery;
    }

    /// <summary>503 on every attempt, spaced like the demo backoff (2, 4, 8, 16 s). Retrying keeps a next attempt in the future.</summary>
    private static Delivery Failing(Destination destination, DateTimeOffset receivedAt, int attempts, bool deadLettered, DateTimeOffset now)
    {
        var delivery = new Delivery
        {
            DestinationId = destination.Id,
            Status = deadLettered ? DeliveryStatus.DeadLettered : DeliveryStatus.Retrying,
            AttemptCount = attempts,
            LastError = DemoSinkConnector.OutageError,
            NextAttemptAt = deadLettered ? null : now + DeliveryDispatcher.DelayAfter(attempts, DeliveryDispatcher.DemoRetryProfile),
        };
        var startedAt = receivedAt.AddSeconds(1);
        for (var n = 1; n <= attempts; n++)
        {
            delivery.Attempts.Add(new DeliveryAttempt
            {
                AttemptNo = n, StartedAt = startedAt, DurationMs = Random.Shared.Next(40, 121), HttpStatus = 503, Error = DemoSinkConnector.OutageError,
            });
            startedAt += DeliveryDispatcher.DelayAfter(n, DeliveryDispatcher.DemoRetryProfile);
        }
        return delivery;
    }
}
