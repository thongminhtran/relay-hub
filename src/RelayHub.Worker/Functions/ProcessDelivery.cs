using System.Diagnostics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Messaging;
using RelayHub.Core.Telemetry;
using RelayHub.Infrastructure.Processing;

namespace RelayHub.Worker.Functions;

/// <summary>
/// One message = one delivery attempt. Connector failures never throw: the processor re-enqueues with the backoff delay
/// or dead-letters and alerts. Only crashes bubble up to the host (retry, then <c>deliveries-poison</c>).
/// </summary>
public sealed class ProcessDelivery(DeliveryProcessor processor, ILogger<ProcessDelivery> log)
{
    [Function("ProcessDelivery")]
    public async Task RunAsync([QueueTrigger("deliveries", Connection = "AzureWebJobsStorage")] string body, CancellationToken ct)
    {
        var msg = QueueJson.Deserialize<DeliveryMessage>(body)
                  ?? throw new InvalidOperationException("deliveries message is not a valid DeliveryMessage");

        using var activity = RelayHubTelemetry.Source.StartActivity("worker.delivery", ActivityKind.Consumer, msg.TraceParent);
        activity?.SetTag("relayhub.correlation_id", msg.CorrelationId);
        activity?.SetTag("relayhub.delivery_id", msg.DeliveryId);
        activity?.SetTag("relayhub.reason", msg.Reason);
        using var scope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = msg.CorrelationId, ["DeliveryId"] = msg.DeliveryId });

        var outcome = await processor.ProcessAsync(msg, ct);
        activity?.SetTag("relayhub.outcome", outcome.ToString());
        log.LogInformation("delivery {DeliveryId} ({Reason}, after attempt {AfterAttempt}): {Outcome}", msg.DeliveryId, msg.Reason, msg.AfterAttempt, outcome);
    }
}
