using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RelayHub.Api.Auth;
using RelayHub.Api.Startup;
using RelayHub.Core.Demo;
using RelayHub.Core.Domain;
using RelayHub.Core.Messaging;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Connectors;
using RelayHub.Infrastructure.Data;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

/// <summary>
/// The public demo's controls (plan section 9): simulate a provider webhook that flows through the real pipeline, flip the
/// "Simulate failure" switch, reset the sample data. Every endpoint requires the ambient workspace to be the demo workspace;
/// nothing here can touch the Studio.
/// </summary>
public static class DemoEndpoints
{
    public const string DemoOnlyProblemType = "https://relayhub.dev/problems/demo-only";
    public const string DemoUnavailableProblemType = "https://relayhub.dev/problems/demo-unavailable";
    private const string DemoWorkspaceItem = "DemoWorkspace";

    public sealed record SimulateRequest(string? Channel, string? Text, string? SenderName);
    public sealed record FailureModeRequest(bool Enabled);

    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/demo").WithTags("Demo").RequireAuthorization(AuthPolicies.Member).AddEndpointFilter(RequireDemoWorkspaceAsync);

        group.MapPost("/simulate", SimulateAsync).WithName("DemoSimulate")
             .WithSummary("Simulate an inbound message on instagram | facebook | telegram | email | web: a provider-shaped payload is queued exactly like a verified webhook. Demo workspace only; 20 per minute per IP.")
             .RequireRateLimiting(DemoRateLimiting.SimulatePolicy)
             .Produces(StatusCodes.Status202Accepted)
             .ProducesValidationProblem()
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/failure-mode", GetFailureModeAsync).WithName("DemoGetFailureMode")
             .WithSummary("Whether the flaky demo destination is currently failing (503 on every attempt).");
        group.MapPut("/failure-mode", SetFailureModeAsync).WithName("DemoSetFailureMode")
             .WithSummary("Turn the flaky demo destination's outage on or off; with it on, website simulations retry 2/4/8/16/32 s and dead-letter.");
        group.MapPost("/reset", ResetAsync).WithName("DemoReset")
             .WithSummary("Delete every event and conversation of the demo workspace, turn the failure switch off and re-seed the sample data.");

        return app;
    }

    /// <summary>403 for any workspace that is not the demo; otherwise stores the workspace for the handlers.</summary>
    private static async ValueTask<object?> RequireDemoWorkspaceAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ambient = http.RequestServices.GetRequiredService<AmbientWorkspace>();
        var db = http.RequestServices.GetRequiredService<RelayHubDbContext>();
        var workspaceId = ambient.WorkspaceIdOrDefault();

        var workspace = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workspaceId, http.RequestAborted);
        if (workspace is null || !workspace.IsDemo)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, type: DemoOnlyProblemType,
                title: "Demo workspace only", detail: "The demo endpoints only work in the public demo workspace.");
        }

        http.Items[DemoWorkspaceItem] = workspace;
        return await next(context);
    }

    private static Workspace DemoWorkspace(HttpContext http) => (Workspace)http.Items[DemoWorkspaceItem]!;

    private static async Task<IResult> SimulateAsync(SimulateRequest request, HttpContext http, RelayHubDbContext db, IRelayQueues queues, CancellationToken ct)
    {
        var demo = DemoWorkspace(http);

        var channel = request.Channel?.Trim().ToLowerInvariant() ?? "";
        if (!DemoChannels.IsKnown(channel))
            return ValidationProblem("channel", $"Unknown channel. Allowed: {string.Join(", ", DemoChannels.All)}");
        var text = request.Text?.Trim();
        if (text is { Length: > DemoCatalog.MaxSimulatedTextLength })
            return ValidationProblem("text", $"Text must be at most {DemoCatalog.MaxSimulatedTextLength} characters");
        if (request.SenderName is { Length: > 100 })
            return ValidationProblem("senderName", "Sender name must be at most 100 characters");

        var sourceName = SourceNameFor(channel);
        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Name == sourceName, ct);
        if (source is null || !source.IsEnabled)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: DemoUnavailableProblemType,
                title: "Demo source unavailable", detail: $"Source \"{sourceName}\" is missing or disabled. POST /api/v1/demo/reset recreates it.");
        }

        // Plan 9.3 cap: at most 500 demo events; make room before accepting a new one.
        await DemoSeeder.TrimOldestAsync(db, demo.Id, ct);

        var sender = DemoCustomers.Resolve(request.SenderName);
        if (string.IsNullOrEmpty(text)) text = DemoPayloads.DefaultText(channel);
        var now = DateTimeOffset.UtcNow;
        var simulated = DemoPayloads.Build(channel, sender, text, source.KeyId, now);
        var correlationId = http.Items["CorrelationId"] as string ?? Guid.NewGuid().ToString("N");

        // Same path as a verified hook: queue first, the worker parses, stores and routes.
        await queues.EnqueueIncomingAsync(
            new IncomingMessage(simulated.KeyId, simulated.SourceKey, simulated.ExternalId, simulated.PayloadJson, now, correlationId, Activity.Current?.Id, simulated.Kind), ct);

        return Results.Accepted(value: new
        {
            correlationId,
            channel,
            sender = new { name = sender.Name, handle = sender.Handle, email = sender.Email },
            text,
            hint = "Watch the inbox — the worker stores it in a few seconds.",
        });
    }

    private static async Task<IResult> GetFailureModeAsync(RelayHubDbContext db, CancellationToken ct)
    {
        var flaky = await FlakyDestinations(db).AsNoTracking().ToListAsync(ct);
        return Results.Ok(new { enabled = flaky.Any(d => DemoSinkConnector.IsFailing(d.ConfigJson)) });
    }

    private static async Task<IResult> SetFailureModeAsync(FailureModeRequest request, RelayHubDbContext db, CancellationToken ct)
    {
        var changed = await SetFailingAsync(db, request.Enabled, ct);
        if (changed == 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: DemoUnavailableProblemType,
                title: "No flaky destination", detail: $"No destination named *{DemoCatalog.FlakyNameMarker}* exists in the demo workspace. POST /api/v1/demo/reset recreates it.");
        }
        return Results.Ok(new { enabled = request.Enabled });
    }

    private static async Task<IResult> ResetAsync(HttpContext http, RelayHubDbContext db, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var demo = DemoWorkspace(http);

        await DemoSeeder.ClearAsync(db, demo.Id, ct);
        await SetFailingAsync(db, false, ct);
        var summary = await DemoSeeder.RunAsync(db, loggerFactory.CreateLogger(typeof(DemoEndpoints)), ct);

        return Results.Ok(new { status = "reset", summary.Conversations, summary.Events, failureMode = false });
    }

    /// <summary>Sets <c>failing</c> on every destination whose name contains "Flaky", keeping the other config keys. Returns how many were updated.</summary>
    private static async Task<int> SetFailingAsync(RelayHubDbContext db, bool enabled, CancellationToken ct)
    {
        var flaky = await FlakyDestinations(db).ToListAsync(ct);
        foreach (var destination in flaky)
        {
            var config = TryParseObject(destination.ConfigJson) ?? new JsonObject();
            config[DemoSinkConnector.FailingProperty] = enabled;
            destination.ConfigJson = config.ToJsonString();
        }
        if (flaky.Count > 0) await db.SaveChangesAsync(ct);
        return flaky.Count;
    }

    private static IQueryable<Destination> FlakyDestinations(RelayHubDbContext db)
        => db.Destinations.Where(d => d.Name.Contains(DemoCatalog.FlakyNameMarker));

    private static JsonObject? TryParseObject(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string SourceNameFor(string channel) => channel switch
    {
        DemoChannels.Instagram => DemoCatalog.InstagramSource,
        DemoChannels.Facebook => DemoCatalog.FacebookSource,
        DemoChannels.Telegram => DemoCatalog.TelegramSource,
        DemoChannels.Email => DemoCatalog.EmailSource,
        _ => DemoCatalog.WebsiteSource,
    };
}
