using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Abstractions;

namespace RelayHub.Worker.Functions;

/// <summary>
/// The Functions host moves a message to <c>{queue}-poison</c> after it crashed the function maxDequeueCount times.
/// These functions make that visible: an error log plus a Discord alert to the operator. They never throw.
/// </summary>
public sealed class PoisonAlerts(IOpsAlerts alerts, ILogger<PoisonAlerts> log)
{
    private const int BodyPreviewLength = 600;

    [Function("IncomingPoison")]
    public Task IncomingAsync([QueueTrigger("incoming-poison", Connection = "AzureWebJobsStorage")] string body, CancellationToken ct)
        => AlertAsync("incoming", body, ct);

    [Function("DeliveriesPoison")]
    public Task DeliveriesAsync([QueueTrigger("deliveries-poison", Connection = "AzureWebJobsStorage")] string body, CancellationToken ct)
        => AlertAsync("deliveries", body, ct);

    private async Task AlertAsync(string queue, string body, CancellationToken ct)
    {
        var preview = body.Length > BodyPreviewLength ? body[..BodyPreviewLength] + "…" : body;
        log.LogError("Poison message on queue {Queue}: {Body}", queue, preview);
        await alerts.NotifyAsync($"Poison message on queue '{queue}'",
            $"The worker crashed repeatedly on this message; it is parked on '{queue}-poison' and needs a human. Body: {preview}", ct);
    }
}
