using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayHub.Api.Startup;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;
using RelayHub.Core.Security;
using RelayHub.Core.Templates;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;

namespace RelayHub.Api.Endpoints;

public static class IngestEndpoints
{
    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/hooks/{sourceKey}", IngestGenericAsync)
           .WithName("IngestGeneric")
           .WithTags("Ingest")
           .WithSummary("Generic source ingest (e.g. the website contact form). Requires X-Api-Key.")
           .Accepts<JsonElement>("application/json")
           .Produces(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status401Unauthorized);
        return app;
    }

    // Week 1: store + deliver synchronously. Week 2 moves delivery behind Storage Queues + the Functions worker.
    private static async Task<IResult> IngestGenericAsync(
        string sourceKey,
        HttpRequest request,
        RelayHubDbContext db,
        AmbientWorkspace ambient,
        IOptions<RelayHubOptions> options,
        IEnumerable<IDestinationConnector> connectors,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("RelayHub.Ingest");
        var correlationId = request.HttpContext.Items["CorrelationId"] as string ?? Guid.NewGuid().ToString("N");

        if (!ApiKeys.TryVerify(request.Headers["X-Api-Key"], options.Value.ApiKeyMasterKey, out var keyId))
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid API key");

        var source = await db.Sources.IgnoreQueryFilters()
            .SingleOrDefaultAsync(s => s.KeyId == keyId && s.IsEnabled, ct);
        if (source is null || !string.Equals(source.Name, sourceKey, StringComparison.OrdinalIgnoreCase))
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "API key does not match this source");

        ambient.WorkspaceId = source.WorkspaceId;

        JsonElement payload;
        try { payload = await request.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a JSON object"); }
        if (payload.ValueKind != JsonValueKind.Object)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a JSON object");

        var name = GetString(payload, "name") ?? "Unknown";
        var email = GetString(payload, "email");
        var text = GetString(payload, "message") ?? GetString(payload, "text") ?? "";
        var externalId = request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(externalId)) externalId = Guid.NewGuid().ToString("N");

        var threadKey = (email ?? name).Trim().ToLowerInvariant();
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(c => c.SourceId == source.Id && c.ExternalThreadId == threadKey, ct);
        if (conversation is null)
        {
            conversation = new Conversation { WorkspaceId = source.WorkspaceId, SourceId = source.Id, ExternalThreadId = threadKey, SenderName = name, SenderHandle = email };
            db.Conversations.Add(conversation);
        }
        conversation.LastInboundAt = DateTimeOffset.UtcNow;

        var evt = new Event
        {
            WorkspaceId = source.WorkspaceId,
            SourceId = source.Id,
            ExternalId = externalId,
            Channel = source.Type.ToString().ToLowerInvariant() == "generic" ? "web" : source.Type.ToString().ToLowerInvariant(),
            SenderName = name,
            SenderHandle = email,
            Text = text,
            PayloadJson = payload.GetRawText(),
            CorrelationId = correlationId,
        };
        db.Events.Add(evt);

        try
        {
            await db.SaveChangesAsync(ct);
            evt.ConversationId = conversation.Id;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            log.LogInformation("Duplicate event ignored (source {Source}, externalId {ExternalId})", source.Name, externalId);
            return Results.Accepted(value: new { status = "duplicate", correlationId });
        }

        var routes = await db.Routes.Where(r => r.SourceId == source.Id && r.IsEnabled).ToListAsync(ct);
        var destinationIds = routes.Select(r => r.DestinationId).ToList();
        var destinations = await db.Destinations.Where(d => destinationIds.Contains(d.Id) && d.IsEnabled).ToDictionaryAsync(d => d.Id, ct);

        var values = new Dictionary<string, string?>
        {
            ["name"] = name, ["email"] = email, ["text"] = text, ["channel"] = evt.Channel, ["source"] = source.Name,
        };

        foreach (var route in routes)
        {
            if (!destinations.TryGetValue(route.DestinationId, out var destination)) continue;

            var delivery = new Delivery { EventId = evt.Id, DestinationId = destination.Id };
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync(ct);

            var connector = connectors.FirstOrDefault(c => c.Type == destination.Type);
            var rendered = TemplateRenderer.Render(route.Template, values);
            var sw = Stopwatch.StartNew();
            DeliveryResult result;
            try
            {
                result = connector is null
                    ? new DeliveryResult(false, null, $"No connector for {destination.Type}", Retryable: false)
                    : await connector.SendAsync(destination, rendered, evt, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                result = new DeliveryResult(false, null, ex.Message);
            }
            sw.Stop();

            delivery.AttemptCount = 1;
            delivery.Attempts.Add(new DeliveryAttempt { AttemptNo = 1, DurationMs = (int)sw.ElapsedMilliseconds, HttpStatus = result.HttpStatus, Error = result.Error });
            if (result.Succeeded)
            {
                delivery.Status = DeliveryStatus.Delivered;
            }
            else
            {
                delivery.LastError = result.Error;
                delivery.Status = result.Retryable ? DeliveryStatus.Retrying : DeliveryStatus.DeadLettered;
                delivery.NextAttemptAt = result.Retryable ? DateTimeOffset.UtcNow.AddSeconds(30) : null; // picked up by the worker (Week 2)
                log.LogWarning("Delivery {DeliveryId} to {Destination} failed: {Error}", delivery.Id, destination.Name, result.Error);
            }
            await db.SaveChangesAsync(ct);
        }

        return Results.Accepted(value: new { status = "accepted", correlationId, eventId = evt.Id, deliveries = routes.Count });
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is SqlException { Number: 2601 or 2627 };
}
