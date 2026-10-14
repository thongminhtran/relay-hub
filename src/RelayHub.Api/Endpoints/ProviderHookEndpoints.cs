using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RelayHub.Core.Messaging;
using RelayHub.Core.Security;
using RelayHub.Infrastructure.Channels;
using static RelayHub.Api.Endpoints.EndpointHelpers;

namespace RelayHub.Api.Endpoints;

/// <summary>
/// Webhooks the providers call (no API key, their own signature scheme): Meta (Facebook Page + Instagram), Telegram, and our
/// Cloudflare Email Worker. Same rule as the generic hook: verify, enqueue one <see cref="IncomingMessage"/>, answer in
/// milliseconds, never touch SQL. The worker's parsers find the source by ExternalAccountId / source key.
/// </summary>
public static class ProviderHookEndpoints
{
    /// <summary>Same ceiling as the generic hook: a Storage Queue message holds 64 KB, ~48 KB of raw JSON once Base64-encoded with the envelope.</summary>
    private const int MaxBodyBytes = 32 * 1024;

    public const string MetaSourceKey = "meta";
    public const string EmailSourceKey = "email";

    public static IEndpointRouteBuilder MapProviderHookEndpoints(this IEndpointRouteBuilder app)
    {
        var hooks = app.MapGroup("/api/v1/hooks").WithTags("Ingest").AllowAnonymous();

        hooks.MapGet("/meta", VerifyMetaAsync)
             .WithName("VerifyMetaWebhook")
             .WithSummary("Meta webhook verification handshake (hub.mode=subscribe, hub.verify_token, hub.challenge). Answers the challenge when the token matches RelayHub:Meta:VerifyToken.")
             .Produces(StatusCodes.Status200OK, contentType: "text/plain")
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        hooks.MapPost("/meta", IngestMetaAsync)
             .WithName("IngestMeta")
             .WithSummary("Meta webhook (Facebook Page messaging + Instagram messaging). Verifies X-Hub-Signature-256 over the raw body with RelayHub:Meta:AppSecret and queues the batch. Meta requires a 200.")
             .Accepts<JsonElement>("application/json")
             .Produces(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        hooks.MapPost("/telegram/{sourceKey}", IngestTelegramAsync)
             .WithName("IngestTelegram")
             .WithSummary("Telegram bot webhook. {sourceKey} is the Telegram source's name; X-Telegram-Bot-Api-Secret-Token must equal RelayHub:Telegram:WebhookSecret (the secret_token given to setWebhook).")
             .Accepts<JsonElement>("application/json")
             .Produces(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        hooks.MapPost("/email", IngestEmailAsync)
             .WithName("IngestEmail")
             .WithSummary("Inbound email forwarded by the Cloudflare Email Worker: { to, from, fromName?, subject?, text, html?, messageId, inReplyTo?, receivedAt? }, signed with X-RelayHub-Signature (HMAC-SHA256 of the raw body with RelayHub:Email:InboundSecret).")
             .Accepts<JsonElement>("application/json")
             .Produces(StatusCodes.Status202Accepted)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
             .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    // GET /api/v1/hooks/meta?hub.mode=subscribe&hub.verify_token=...&hub.challenge=...
    private static IResult VerifyMetaAsync(HttpRequest request, IOptions<ChannelOptions> options)
    {
        var verifyToken = options.Value.Meta.VerifyToken;
        if (string.IsNullOrWhiteSpace(verifyToken)) return NotConfigured("Meta", "RelayHub:Meta:VerifyToken");

        var mode = request.Query["hub.mode"].ToString();
        var token = request.Query["hub.verify_token"].ToString();
        var challenge = request.Query["hub.challenge"].ToString();
        if (mode != "subscribe" || !WebhookSignatures.SecretEquals(token, verifyToken) || string.IsNullOrEmpty(challenge))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Verify token does not match");

        return Results.Text(challenge, "text/plain");
    }

    private static async Task<IResult> IngestMetaAsync(HttpRequest request, IOptions<ChannelOptions> options, IRelayQueues queues, CancellationToken ct)
    {
        var appSecret = options.Value.Meta.AppSecret;
        if (string.IsNullOrWhiteSpace(appSecret)) return NotConfigured("Meta", "RelayHub:Meta:AppSecret");

        var (raw, tooLarge) = await ReadRawBodyAsync(request, ct);
        if (tooLarge) return PayloadTooLarge();
        // The signature covers the exact bytes Meta sent: verify before parsing, on the raw body.
        if (!WebhookSignatures.IsValid(request.Headers["X-Hub-Signature-256"], appSecret, raw))
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid X-Hub-Signature-256");

        if (!IsJsonObject(raw, out var payloadJson))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a JSON object");

        await EnqueueAsync(request, queues, MetaSourceKey, WebhookSignatures.BodyHashId(raw), payloadJson, IncomingKinds.Meta, ct);
        return Results.Ok(new { });
    }

    private static async Task<IResult> IngestTelegramAsync(string sourceKey, HttpRequest request, IOptions<ChannelOptions> options, IRelayQueues queues, CancellationToken ct)
    {
        var secret = options.Value.Telegram.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret)) return NotConfigured("Telegram", "RelayHub:Telegram:WebhookSecret");

        if (!WebhookSignatures.SecretEquals(request.Headers["X-Telegram-Bot-Api-Secret-Token"], secret))
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid X-Telegram-Bot-Api-Secret-Token");

        sourceKey = sourceKey.ToLowerInvariant();
        if (!SlugPattern().IsMatch(sourceKey))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid source key");

        var (raw, tooLarge) = await ReadRawBodyAsync(request, ct);
        if (tooLarge) return PayloadTooLarge();
        if (!IsJsonObject(raw, out var payloadJson))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a JSON object");

        var updateId = ReadNumberOrString(payloadJson, "update_id");
        if (updateId is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a Telegram Update with update_id");

        await EnqueueAsync(request, queues, sourceKey, $"tg:{updateId}", payloadJson, IncomingKinds.Telegram, ct);
        return Results.Ok(new { });
    }

    private static async Task<IResult> IngestEmailAsync(HttpRequest request, IOptions<ChannelOptions> options, IRelayQueues queues, CancellationToken ct)
    {
        var secret = options.Value.Email.InboundSecret;
        if (string.IsNullOrWhiteSpace(secret)) return NotConfigured("Inbound email", "RelayHub:Email:InboundSecret");

        var (raw, tooLarge) = await ReadRawBodyAsync(request, ct);
        if (tooLarge) return PayloadTooLarge();
        if (!WebhookSignatures.IsValid(request.Headers["X-RelayHub-Signature"], secret, raw))
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid X-RelayHub-Signature");

        if (!IsJsonObject(raw, out var payloadJson))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Body must be a JSON object");

        // Message-ID is the natural dedupe key; a mail without one (rare, usually spam) still gets a stable id from its bytes.
        var externalId = ReadNumberOrString(payloadJson, "messageId")?.Trim();
        if (string.IsNullOrEmpty(externalId)) externalId = WebhookSignatures.BodyHashId(raw);

        var correlationId = await EnqueueAsync(request, queues, EmailSourceKey, externalId, payloadJson, IncomingKinds.Email, ct);
        return Results.Accepted(value: new { status = "queued", correlationId });
    }

    private static async Task<string> EnqueueAsync(HttpRequest request, IRelayQueues queues, string sourceKey, string externalId, string payloadJson, string kind, CancellationToken ct)
    {
        var correlationId = request.HttpContext.Items["CorrelationId"] as string ?? Guid.NewGuid().ToString("N");
        if (externalId.Length > 200) externalId = externalId[..200];
        await queues.EnqueueIncomingAsync(
            new IncomingMessage(KeyId: "", sourceKey, externalId, payloadJson, DateTimeOffset.UtcNow, correlationId, Activity.Current?.Id, kind), ct);
        return correlationId;
    }

    private static IResult NotConfigured(string channel, string setting)
        => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: $"{channel} channel not configured", detail: $"Set {setting} to enable this hook.");

    private static IResult PayloadTooLarge()
        => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: $"Payload must be at most {MaxBodyBytes / 1024} KB");

    /// <summary>Reads the body as bytes (signatures are computed over the exact bytes). Chunked requests carry no Content-Length, so the limit is enforced while reading.</summary>
    private static async Task<(byte[] Body, bool TooLarge)> ReadRawBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes) return ([], true);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return ([], true);
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    private static bool IsJsonObject(byte[] raw, out string payloadJson)
    {
        payloadJson = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            payloadJson = doc.RootElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadNumberOrString(string payloadJson, string property)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        if (!doc.RootElement.TryGetProperty(property, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString(),
            _ => null,
        };
    }
}
