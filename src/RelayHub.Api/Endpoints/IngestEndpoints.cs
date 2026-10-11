using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayHub.Api.Startup;
using RelayHub.Core.Domain;
using RelayHub.Core.Security;
using RelayHub.Infrastructure;
using RelayHub.Infrastructure.Data;
using RelayHub.Infrastructure.Dispatch;

namespace RelayHub.Api.Endpoints;

public static class IngestEndpoints
{
    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/hooks/{sourceKey}", IngestGenericAsync)
           .WithName("IngestGeneric")
           .WithTags("Ingest")
           .WithSummary("Generic source ingest (e.g. the website contact form). Requires X-Api-Key; Idempotency-Key is honoured.")
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
        DeliveryDispatcher dispatcher,
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
        if (externalId.Length > 200) externalId = externalId[..200];

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
            Channel = source.Type == SourceType.Generic ? "web" : source.Type.ToString().ToLowerInvariant(),
            SenderName = name,
            SenderHandle = email,
            Text = text.Length > 4000 ? text[..4000] : text,
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

        var deliveries = await dispatcher.DispatchAsync(evt, source, ct);

        return Results.Accepted(value: new { status = "accepted", correlationId, eventId = evt.Id, deliveries });
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is SqlException { Number: 2601 or 2627 };
}
