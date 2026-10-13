using System.Diagnostics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using RelayHub.Core.Messaging;
using RelayHub.Core.Telemetry;
using RelayHub.Infrastructure.Processing;

namespace RelayHub.Worker.Functions;

/// <summary>
/// Picks raw webhook payloads off the <c>incoming</c> queue: resolves the source, stores the Event (duplicates ignored),
/// creates one Pending Delivery per route and queues each of them. Throws only on infrastructure failures (bad JSON, SQL down),
/// so the host retries (host.json maxDequeueCount) and finally parks the message on <c>incoming-poison</c>.
/// </summary>
public sealed class ProcessIncoming(IncomingProcessor processor, ILogger<ProcessIncoming> log)
{
    [Function("ProcessIncoming")]
    public async Task RunAsync([QueueTrigger("incoming", Connection = "AzureWebJobsStorage")] string body, CancellationToken ct)
    {
        var msg = QueueJson.Deserialize<IncomingMessage>(body)
                  ?? throw new InvalidOperationException("incoming message is not a valid IncomingMessage");

        using var activity = RelayHubTelemetry.Source.StartActivity("worker.incoming", ActivityKind.Consumer, msg.TraceParent);
        activity?.SetTag("relayhub.correlation_id", msg.CorrelationId);
        activity?.SetTag("relayhub.source_key", msg.SourceKey);
        using var scope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = msg.CorrelationId });

        var outcome = await processor.ProcessAsync(msg, ct);
        activity?.SetTag("relayhub.outcome", outcome.ToString());
        log.LogInformation("incoming {SourceKey}/{ExternalId}: {Outcome}", msg.SourceKey, msg.ExternalId, outcome);
    }
}
