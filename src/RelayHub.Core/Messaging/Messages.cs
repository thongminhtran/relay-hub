using System.Text.Json;

namespace RelayHub.Core.Messaging;

/// <summary>Serializer settings shared by every queue producer and consumer (camelCase, case-insensitive).</summary>
public static class QueueJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>
/// A raw webhook payload accepted by the API and handed to the worker through the <c>incoming</c> queue.
/// The API only verifies the signature and enqueues; everything that needs SQL happens in the worker.
/// </summary>
/// <param name="KeyId">Key id from the verified API key; the worker resolves the source from it.</param>
/// <param name="SourceKey">The {sourceKey} from the URL; must match the source's name or the message is dropped.</param>
/// <param name="ExternalId">Idempotency-Key (or a generated id). Unique per source = at-least-once made safe.</param>
/// <param name="TraceParent">W3C traceparent of the API request, so the worker's spans join the same trace.</param>
public sealed record IncomingMessage(
    string KeyId,
    string SourceKey,
    string ExternalId,
    string PayloadJson,
    DateTimeOffset ReceivedAt,
    string CorrelationId,
    string? TraceParent);

/// <summary>
/// One attempt of one delivery, carried by the <c>deliveries</c> queue. Retries re-enqueue it with a visibility delay;
/// replay enqueues it again by hand.
/// </summary>
/// <param name="AfterAttempt">The delivery's AttemptCount when this message was enqueued. The worker skips the message
/// if the delivery has moved on since (a stale scheduled retry after a replay, or a duplicate).</param>
/// <param name="Reason">initial | retry | replay — for logs and traces only.</param>
public sealed record DeliveryMessage(
    long DeliveryId,
    int WorkspaceId,
    string CorrelationId,
    string? TraceParent,
    int AfterAttempt,
    string Reason);

public static class DeliveryReasons
{
    public const string Initial = "initial";
    public const string Retry = "retry";
    public const string Replay = "replay";
}

/// <summary>The two queues between the API and the worker. Implemented on Azure Storage Queues; faked in tests.</summary>
public interface IRelayQueues
{
    Task EnqueueIncomingAsync(IncomingMessage message, CancellationToken ct);

    /// <param name="visibilityDelay">How long the message stays invisible: the retry backoff. Null = deliver now.</param>
    Task EnqueueDeliveryAsync(DeliveryMessage message, TimeSpan? visibilityDelay, CancellationToken ct);
}
