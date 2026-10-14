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

public sealed record ReplyResult(bool Succeeded, string? ProviderMessageId, int? HttpStatus, string? Error)
{
    public static ReplyResult Ok(string? providerMessageId, int? httpStatus = null) => new(true, providerMessageId, httpStatus, null);
    public static ReplyResult Fail(string error, int? httpStatus = null) => new(false, null, httpStatus, error);
}

/// <summary>
/// Sends an owner's reply back to the customer on the channel the conversation came in on (Meta Send API, Telegram bot,
/// Resend email). Synchronous: the person who pressed Send is waiting (plan decision 11). One implementation per source type.
/// The 24-hour window check for Meta happens in the API before this is called.
/// </summary>
public interface IReplyChannel
{
    SourceType SourceType { get; }
    Task<ReplyResult> SendAsync(Source source, Conversation conversation, string text, CancellationToken ct);
}

/// <summary>Live updates for open dashboards. Implemented on Azure SignalR Service; a no-op when it is not configured. Never throws.</summary>
public interface IRealtimeNotifier
{
    Task EventCreatedAsync(Event evt, CancellationToken ct);
    Task DeliveryUpdatedAsync(Delivery delivery, int workspaceId, CancellationToken ct);
    Task ReplySentAsync(Event reply, CancellationToken ct);
}

public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task EventCreatedAsync(Event evt, CancellationToken ct) => Task.CompletedTask;
    public Task DeliveryUpdatedAsync(Delivery delivery, int workspaceId, CancellationToken ct) => Task.CompletedTask;
    public Task ReplySentAsync(Event reply, CancellationToken ct) => Task.CompletedTask;
}
