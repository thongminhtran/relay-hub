using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace RelayHub.Worker.Functions;

/// <summary>
/// Week 2: picks raw webhook payloads off the <c>incoming</c> queue, normalizes them into Events,
/// stores them, finds matching routes and creates Deliveries. Today it only proves the trigger wiring.
/// </summary>
public sealed class ProcessIncoming(ILogger<ProcessIncoming> log)
{
    [Function("ProcessIncoming")]
    public Task RunAsync([QueueTrigger("incoming", Connection = "AzureWebJobsStorage")] string message, FunctionContext context)
    {
        log.LogInformation("incoming message received ({Length} chars), invocation {InvocationId}", message.Length, context.InvocationId);
        return Task.CompletedTask;
    }
}
