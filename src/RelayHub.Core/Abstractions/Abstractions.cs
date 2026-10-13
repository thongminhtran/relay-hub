using RelayHub.Core.Domain;

namespace RelayHub.Core.Abstractions;

/// <summary>The workspace the current request/job is scoped to. Null = unscoped (system jobs).</summary>
public interface ICurrentWorkspace
{
    int? WorkspaceId { get; }
}

public sealed record DeliveryResult(bool Succeeded, int? HttpStatus, string? Error, bool Retryable = true)
{
    public static DeliveryResult Ok(int? httpStatus = null) => new(true, httpStatus, null);
}

/// <summary>Sends a rendered notification to one kind of destination (Strategy pattern: one class per channel).</summary>
public interface IDestinationConnector
{
    DestinationType Type { get; }
    Task<DeliveryResult> SendAsync(Destination destination, string text, Event evt, CancellationToken ct);
}

/// <summary>Self-alerting: RelayHub tells its operator when something needs a human (dead-letter, poison message). Never throws.</summary>
public interface IOpsAlerts
{
    Task NotifyAsync(string title, string detail, CancellationToken ct);
}
