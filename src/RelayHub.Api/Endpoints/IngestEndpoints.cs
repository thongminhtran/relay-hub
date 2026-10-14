using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RelayHub.Api.Startup;
using RelayHub.Core.Messaging;
using RelayHub.Core.Security;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

public static class IngestEndpoints
{
    /// <summary>Storage Queue messages are limited to 64 KB (48 KB once Base64-encoded); form payloads are tiny. Bigger payloads will use a blob pointer (claim check) when a channel needs it.</summary>
    private const int MaxBodyBytes = 32 * 1024;

    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/hooks/{sourceKey}", IngestGenericAsync)
           .WithName("IngestGeneric")
           .WithTags("Ingest")
           .WithSummary("Generic source ingest (e.g. the website contact form). Requires X-Api-Key; Idempotency-Key is honoured. Verifies and queues; nothing touches the database here.")
           .Accepts<JsonElement>("application/json")
           .Produces(StatusCodes.Status202Accepted)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status401Unauthorized)
           .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        // Provider webhooks (Meta, Telegram, email) live next to the generic hook; literal segments win over {sourceKey} in routing.
        app.MapProviderHookEndpoints();
        return app;
    }

    // Queue-first: verify the signature, enqueue the raw payload, answer 202 within milliseconds.
    // The worker (RelayHub.Worker, ProcessIncoming) resolves the source, stores the event, dedupes and routes.
    private static async Task<IResult> IngestGenericAsync(
        string sourceKey,
        HttpRequest request,
        IOptions<RelayHubOptions> options,
        IRelayQueues queues,
        CancellationToken ct)
    {
        var correlationId = request.HttpContext.Items["CorrelationId"] as string ?? Guid.NewGuid().ToString("N");

        if (!ApiKeys.TryVerify(request.Headers["X-Api-Key"], options.Value.ApiKeyMasterKey, out var keyId))
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid API key");

        sourceKey = sourceKey.ToLowerInvariant();
        if (!SlugPattern().IsMatch(sourceKey))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid source key");

        if (request.ContentLength > MaxBodyBytes)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: $"Payload must be at most {MaxBodyBytes / 1024} KB");

        var raw = await ReadBodyAsync(request.Body, ct);
        if (raw is null)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: $"Payload must be at most {MaxBodyBytes / 1024} KB");

        string payloadJson;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a JSON object");
            payloadJson = doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a JSON object");
        }

        var externalId = request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(externalId)) externalId = Guid.NewGuid().ToString("N");
        if (externalId.Length > 200) externalId = externalId[..200];

        await queues.EnqueueIncomingAsync(
            new IncomingMessage(keyId, sourceKey, externalId, payloadJson, DateTimeOffset.UtcNow, correlationId, Activity.Current?.Id), ct);

        return Results.Accepted(value: new { status = "queued", correlationId });
    }

    /// <summary>Reads the whole body; returns null when it exceeds the limit (chunked requests carry no Content-Length).</summary>
    private static async Task<string?> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
