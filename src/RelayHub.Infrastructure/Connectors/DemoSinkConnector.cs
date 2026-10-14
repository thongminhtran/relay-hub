using System.Text.Json;
using RelayHub.Core.Abstractions;
using RelayHub.Core.Domain;

namespace RelayHub.Infrastructure.Connectors;

/// <summary>
/// Where every delivery of a demo workspace ends up: nothing leaves the system, visitors cannot spam real Discord / Teams /
/// Telegram channels (plan 9.3). Deliberately not an <see cref="IDestinationConnector"/>: it is never picked by destination
/// type, <see cref="Dispatch.DeliveryDispatcher"/> routes to it explicitly when the workspace is a demo. A destination whose
/// ConfigJson says <c>"failing": true</c> answers a retryable 503, which is how the "Simulate failure" switch produces a
/// real retry → dead-letter → replay sequence on the demo backoff (2/4/8/16/32 s).
/// </summary>
public sealed class DemoSinkConnector
{
    public const string FailingProperty = "failing";
    public const string OutageError = "503 simulated outage (demo failure mode)";

    private const int MinSimulatedMs = 40;
    private const int MaxSimulatedMs = 120;

    public async Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct)
    {
        // A short pause so the attempt timeline shows realistic durations instead of 0 ms.
        await Task.Delay(Random.Shared.Next(MinSimulatedMs, MaxSimulatedMs + 1), ct);

        return IsFailing(destination.ConfigJson)
            ? new DeliveryResult(false, 503, OutageError, Retryable: true)
            : DeliveryResult.Ok(200);
    }

    /// <summary>True when the destination config contains <c>"failing": true</c>. Anything unparsable counts as healthy.</summary>
    public static bool IsFailing(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(FailingProperty, out var failing)
                && failing.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
