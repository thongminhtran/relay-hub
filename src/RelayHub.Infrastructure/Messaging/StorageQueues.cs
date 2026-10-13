using Azure;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;
using RelayHub.Core.Messaging;

namespace RelayHub.Infrastructure.Messaging;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Local development: a connection string, e.g. <c>UseDevelopmentStorage=true</c> (Azurite).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Azure: <c>https://{account}.queue.core.windows.net</c>, used with Managed Identity (DefaultAzureCredential).</summary>
    public string? QueueServiceUri { get; set; }

    public string IncomingQueue { get; set; } = "incoming";
    public string DeliveriesQueue { get; set; } = "deliveries";
}

/// <summary>
/// Azure Storage Queues implementation. Messages are Base64-encoded (what the Functions queue trigger expects) and never expire,
/// so nothing is lost while the database is paused or the worker is down. Backoff = visibility delay on re-enqueue.
/// </summary>
public sealed class StorageQueues : IRelayQueues
{
    private static readonly TimeSpan NeverExpire = TimeSpan.FromSeconds(-1);

    private readonly Lazy<QueueClient> _incoming;
    private readonly Lazy<QueueClient> _deliveries;

    public StorageQueues(IOptions<StorageOptions> options)
    {
        // Lazy: resolving the service must never fail (e.g. /health in a test host without storage); only enqueueing may.
        var service = new Lazy<QueueServiceClient>(() => CreateClient(options.Value));
        _incoming = new Lazy<QueueClient>(() => service.Value.GetQueueClient(options.Value.IncomingQueue));
        _deliveries = new Lazy<QueueClient>(() => service.Value.GetQueueClient(options.Value.DeliveriesQueue));
    }

    public Task EnqueueIncomingAsync(IncomingMessage message, CancellationToken ct)
        => SendAsync(_incoming.Value, QueueJson.Serialize(message), null, ct);

    public Task EnqueueDeliveryAsync(DeliveryMessage message, TimeSpan? visibilityDelay, CancellationToken ct)
        => SendAsync(_deliveries.Value, QueueJson.Serialize(message), visibilityDelay, ct);

    private static async Task SendAsync(QueueClient queue, string body, TimeSpan? visibilityDelay, CancellationToken ct)
    {
        try
        {
            await queue.SendMessageAsync(body, visibilityDelay, NeverExpire, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Local Azurite starts empty; in Azure the queues are created by Bicep.
            await queue.CreateIfNotExistsAsync(cancellationToken: ct);
            await queue.SendMessageAsync(body, visibilityDelay, NeverExpire, ct);
        }
    }

    private static QueueServiceClient CreateClient(StorageOptions o)
    {
        var clientOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
            return new QueueServiceClient(o.ConnectionString, clientOptions);
        if (!string.IsNullOrWhiteSpace(o.QueueServiceUri))
            return new QueueServiceClient(new Uri(o.QueueServiceUri), new DefaultAzureCredential(), clientOptions);
        throw new InvalidOperationException(
            "Configure Storage:ConnectionString (local, e.g. UseDevelopmentStorage=true) or Storage:QueueServiceUri (Azure, Managed Identity).");
    }
}
